#pragma once

// Included after the test fixture helpers in Program.cpp.
template<class Predicate>
bool WaitForRecovery(Predicate&& predicate) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    do {
        if (predicate()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    } while (std::chrono::steady_clock::now() < deadline);
    return predicate();
}

void TestPartitionedRecovery() {
    using namespace luvletter::indexer;
    using namespace luvletter::indexing;
    using namespace luvletter::indexing::protocol;
    TemporaryDirectory temporary;
    const auto root = temporary.Path() / L"offline-root";
    const auto onlineRoot = temporary.Path() / L"online-root";
    const auto data = temporary.Path() / L"store";
    std::filesystem::create_directories(root);
    std::filesystem::create_directories(onlineRoot);
    Expect(CreateEmptyFile(root / L"base-retained.txt"), L"recovery base fixture should create");
    Expect(CreateEmptyFile(root / L"base-deleted.txt"), L"recovery deletion fixture should create");
    Expect(CreateEmptyFile(onlineRoot / L"online-retained.txt"), L"online partition fixture should create");
    const std::vector<IndexPartitionDescriptor> descriptors{
        {"filesystem:recovery", root, {}, PartitionMaintenanceTier::StartupCritical,
            std::chrono::hours(1), std::chrono::hours(1)},
        {"filesystem:online", onlineRoot, {}, PartitionMaintenanceTier::Normal,
            std::chrono::hours(1), std::chrono::hours(1)}};
    const auto configure = [&](PartitionedIndexStore& store) {
        return store.Configure(descriptors, std::chrono::hours(1), {}, {}, {});
    };
    const auto silentLog = [](std::string_view, std::string_view) {};
    auto generationDirectory = data / L"partitions" / Utf8ToWide(PartitionCacheName(descriptors[0].id));
    generationDirectory.replace_extension();
    std::optional<IndexBaseIdentity> baseIdentity;
    bool validStatus = true;
    const auto statusOf = [&](PartitionedIndexStore& store) {
        const auto status = store.Status();
        IndexStatus decoded{};
        validStatus &= DecodeStatus(EncodeStatus(status), decoded);
        return status;
    };
    {
        PartitionedIndexStore store(data, silentLog);
        Expect(configure(store), L"partition store should accept recovery fixtures");
        const auto built = WaitForRecovery([&] { return statusOf(store).activity == IndexActivity::Ready; });
        Expect(built, L"both partitions should finish their first persisted generation");
        if (!built) return;
        Expect(store.Query(L"base-retained", 10).size() == 1, L"first base should be queryable");
        baseIdentity = recovery::LoadManifest(generationDirectory);
        Expect(baseIdentity.has_value(), L"first generation should publish its manifest");
        if (!baseIdentity) return;

        Expect(CreateEmptyFile(root / L"journal-added.txt"), L"watcher upsert fixture should create");
        Expect(DeleteFileW((root / L"base-deleted.txt").c_str()) != FALSE,
            L"watcher deletion fixture should delete");
        const auto changed = WaitForRecovery([&] {
            (void)statusOf(store);
            return store.Query(L"journal-added", 10).size() == 1 &&
                store.Query(L"base-deleted", 10).empty();
        });
        Expect(changed, L"watcher should apply both the upsert and tombstone to the live view");
        if (!changed) return;
        Expect(recovery::LoadManifest(generationDirectory) == baseIdentity,
            L"ordinary watcher mutations should remain in the journal without rebuilding the base");
    }
    const auto base = IndexSnapshot::Load(recovery::SnapshotPath(generationDirectory, *baseIdentity));
    Expect(base != nullptr, L"persisted generation should load independently");
    if (!base) return;
    std::vector<DeltaBatch> batches;
    RecoveryJournalOpenStatus journalStatus{};
    auto journal = RecoveryJournal::Open(recovery::JournalPath(generationDirectory, *baseIdentity),
        {*baseIdentity, base->RootsFingerprint(), recovery::kEnumerationPolicyFingerprint,
            base->AppliedDeltaSequence()}, batches, journalStatus);
    Expect(journal != nullptr && journalStatus == RecoveryJournalOpenStatus::Ready && !batches.empty(),
        L"watcher changes must be durable complete batches bound to the unchanged base");
    journal.reset();

    // Disconnect the root only after the watcher stops: no synthetic removal events
    // should hide the exact base and Delta view that restart must recover.
    std::filesystem::rename(root, temporary.Path() / L"disconnected-root");
    {
        PartitionedIndexStore store(data, silentLog);
        Expect(configure(store), L"restart should accept an unavailable configured root");
        const auto recovered = WaitForRecovery([&] {
            return statusOf(store).activity == IndexActivity::Failed &&
                store.Query(L"journal-added", 10).size() == 1 &&
                store.Query(L"online-retained", 10).size() == 1;
        });
        Expect(recovered, L"restart should replay the unavailable partition while its online sibling remains usable");
        Expect(store.Query(L"base-retained", 10).size() == 1,
            L"unavailable-root reconciliation must retain the previous base");
        Expect(store.Query(L"base-deleted", 10).empty(),
            L"journal tombstones must survive recovery and failed reconciliation");
        Expect(recovery::LoadManifest(generationDirectory) == baseIdentity,
            L"failed reconciliation must not replace the unavailable partition manifest");
    }

    // A validly checksummed manifest that names another identity must not publish
    // a snapshot merely because a file exists at the requested generation path.
    const auto wrongIdentity = CreateIndexBaseIdentity();
    Expect(CopyFileW(recovery::SnapshotPath(generationDirectory, *baseIdentity).c_str(),
        recovery::SnapshotPath(generationDirectory, wrongIdentity).c_str(), FALSE) != FALSE,
        L"identity mismatch fixture should copy the immutable base");
    Expect(recovery::SaveManifest(generationDirectory, wrongIdentity),
        L"identity mismatch fixture should publish a checksummed manifest");
    auto backupManifest = recovery::ManifestPath(generationDirectory); backupManifest += L".bak";
    Expect(DeleteFileW(backupManifest.c_str()) != FALSE,
        L"identity mismatch fixture should remove the intentionally valid fallback");
    {
        PartitionedIndexStore store(data, silentLog);
        Expect(configure(store), L"identity mismatch should be handled during recovery");
        Expect(WaitForRecovery([&] { return statusOf(store).activity == IndexActivity::Failed; }),
            L"a mismatched base with an unavailable root should require reconciliation");
        Expect(store.Query(L"base-retained", 10).empty() && store.Query(L"journal-added", 10).empty(),
            L"an identity-mismatched generation must never become the live view");
        Expect(store.Query(L"online-retained", 10).size() == 1,
            L"an invalid generation must not prevent another partition from loading");
    }
    Expect(validStatus, L"every observed queued, working, ready, or failed status must round-trip over LLIX v7");
}
