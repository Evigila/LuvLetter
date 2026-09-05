#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"
#include "IndexMaintenance.h"
#include "PartitionedIndexStore.h"
#include "IndexRebuildPolicy.h"
#include "IndexPartitioning.h"
#include "IndexRecovery.h"
#include "IndexDiagnostics.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <array>
#include <chrono>
#include <cstddef>
#include <condition_variable>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace {

class TemporaryDirectory final {
public:
    TemporaryDirectory() {
        std::array<wchar_t, MAX_PATH> temporaryRoot{};
        GetTempPathW(static_cast<DWORD>(temporaryRoot.size()), temporaryRoot.data());
        path_ = std::filesystem::path(temporaryRoot.data()) /
            (L"LuvLetter.IndexKernel.Tests." + std::to_wstring(GetCurrentProcessId()) + L"." +
             std::to_wstring(GetTickCount64()));
        std::filesystem::create_directories(path_);
    }

    ~TemporaryDirectory() {
        std::error_code ignored;
        std::filesystem::remove_all(path_, ignored);
    }

    [[nodiscard]] const std::filesystem::path& Path() const noexcept { return path_; }

private:
    std::filesystem::path path_;
};

bool CreateEmptyFile(const std::filesystem::path& path) {
    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    CloseHandle(file);
    return true;
}

int failures = 0;

void Expect(const bool condition, const wchar_t* message) {
    if (!condition) {
        std::wcerr << L"FAILED: " << message << L'\n';
        ++failures;
    }
}

bool TruncateLastByte(const std::filesystem::path& path) {
    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER offset{};
    offset.QuadPart = -1;
    const bool succeeded = SetFilePointerEx(file, offset, nullptr, FILE_END) &&
        SetEndOfFile(file) && FlushFileBuffers(file);
    CloseHandle(file);
    return succeeded;
}

bool FlipLastByte(const std::filesystem::path& path) {
    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER offset{};
    offset.QuadPart = -1;
    std::byte value{};
    DWORD read = 0;
    bool succeeded = SetFilePointerEx(file, offset, nullptr, FILE_END) &&
        ReadFile(file, &value, 1, &read, nullptr) && read == 1;
    if (succeeded) {
        value ^= std::byte{0x40};
        offset.QuadPart = -1;
        DWORD written = 0;
        succeeded = SetFilePointerEx(file, offset, nullptr, FILE_END) &&
            WriteFile(file, &value, 1, &written, nullptr) && written == 1 &&
            FlushFileBuffers(file);
    }
    CloseHandle(file);
    return succeeded;
}

bool SameResults(
    const std::span<const luvletter::indexing::SearchResult> left,
    const std::span<const luvletter::indexing::SearchResult> right) {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t index = 0; index < left.size(); ++index) {
        if (left[index].stableId != right[index].stableId ||
            left[index].kind != right[index].kind ||
            left[index].displayName != right[index].displayName ||
            left[index].fullPath != right[index].fullPath) {
            return false;
        }
    }
    return true;
}

#include "Program.Recovery.h"

void TestProtocolHeaderRoundTrip() {
    using namespace luvletter::indexing::protocol;
    const FrameHeader expected{kMagic, kMajorVersion, MessageType::Query, 42, 0x1020304050607080ULL};
    const auto encoded = EncodeHeader(expected);
    FrameHeader actual{};
    Expect(encoded.size() == kHeaderSize, L"protocol header must remain exactly 20 bytes");
    Expect(kMajorVersion == 8, L"manual and forced refresh require LLIX protocol major version 8");
    Expect(static_cast<std::uint16_t>(MessageType::Refresh) == 9,
        L"forced refresh must retain its frame value");
    Expect(static_cast<std::uint16_t>(MessageType::Reconcile) == 10,
        L"normal reconciliation must have a distinct frame value");
    Expect(DecodeHeader(encoded, actual), L"protocol header should decode");
    Expect(actual.magic == expected.magic && actual.majorVersion == expected.majorVersion &&
        actual.type == expected.type && actual.payloadLength == expected.payloadLength &&
        actual.requestId == expected.requestId,
        L"protocol header round-trip should preserve all fields");
}

void TestStatusPayloadRoundTrip() {
    using namespace luvletter::indexing::protocol;
    const IndexStatus expected{
        42,
        IndexActivity::Updating,
        IndexWorkStage::Scanning,
        37,
        kEstimatedProgress,
        123456};
    const auto encoded = EncodeStatus(expected);
    IndexStatus actual{};
    Expect(encoded.size() == 20, L"progress status payload must remain exactly 20 bytes");
    Expect(DecodeStatus(encoded, actual), L"status payload should decode");
    Expect(actual.generation == expected.generation && actual.activity == expected.activity &&
        actual.stage == expected.stage && actual.progressPercent == expected.progressPercent &&
        actual.flags == expected.flags && actual.discoveredEntries == expected.discoveredEntries,
        L"status payload round-trip should preserve progress and activity state");

    auto malformed = encoded;
    malformed[8] = std::byte{4};
    Expect(!DecodeStatus(malformed, actual), L"status payload should reject unknown activity values");
    malformed = encoded;
    malformed[10] = std::byte{101};
    Expect(!DecodeStatus(malformed, actual), L"status payload should reject progress above 100");
    malformed = encoded;
    malformed[9] = std::byte{static_cast<std::uint8_t>(IndexWorkStage::Persisting)};
    Expect(!DecodeStatus(malformed, actual),
        L"estimated progress should only be accepted while scanning");
    const IndexStatus failed{77, IndexActivity::Failed, IndexWorkStage::Idle, kUnknownProgress, 0, 999};
    Expect(DecodeStatus(EncodeStatus(failed), actual),
        L"failed status should use idle stage and unknown progress" );
    const IndexStatus ready{77, IndexActivity::Ready, IndexWorkStage::Idle, 100, 0, 999};
    Expect(DecodeStatus(EncodeStatus(ready), actual),
        L"ready status should require idle stage and complete progress");
}

void TestIndexBuildProgressAndExclusions() {
    using namespace luvletter::indexing;
    TemporaryDirectory temporary;
    const auto visibleDirectory = temporary.Path() / L"visible";
    const auto excludedDirectory = temporary.Path() / L"index-data";
    std::filesystem::create_directories(visibleDirectory);
    std::filesystem::create_directories(excludedDirectory);
    Expect(CreateEmptyFile(visibleDirectory / L"keep.txt"),
        L"visible build fixture should be created");
    Expect(CreateEmptyFile(excludedDirectory / L"internal.delta"),
        L"excluded build fixture should be created");

    std::vector<IndexBuildProgress> observed;
    auto missingRoot = temporary.Path();
    missingRoot += L"-missing-root";
    const std::array roots{temporary.Path(), missingRoot};
    const std::array exclusions{excludedDirectory};
    IndexBuildOptions options{
        17,
        exclusions,
        [&observed](const IndexBuildProgress& progress) {
            auto copy = progress;
            copy.currentPath = {};
            observed.push_back(copy);
        }};
    const auto snapshot = IndexBuilder::Build(roots, nullptr, std::move(options));
    Expect(snapshot != nullptr && snapshot->AppliedDeltaSequence() == 17,
        L"a direct full build should persist its captured Delta sequence without repacking");
    if (!snapshot) return;
    Expect(snapshot->Query(L"keep", 5).size() == 1,
        L"a healthy root should remain indexable beside an unavailable root");
    Expect(snapshot->Query(L"internal", 5).empty(),
        L"index-owned excluded paths must not enter the snapshot");
    Expect(!observed.empty() && std::any_of(observed.begin(), observed.end(), [](const auto& item) {
        return item.stage == IndexBuildStage::Packing;
    }), L"full builds should report scanning and packing stages");
    Expect(std::any_of(observed.begin(), observed.end(), [](const auto& item) {
        return item.rootUnavailable;
    }), L"an unavailable root should be reported without blocking healthy roots");
    Expect(std::all_of(observed.begin(), observed.end(), [](const auto& item) {
        return item.processedDirectories <= item.processedDirectories + item.pendingDirectories;
    }), L"progress counters should remain bounded");
}

void TestDiagnosticLogOptInAndEscaping() {
    using luvletter::indexer::DiagnosticLog;
    Expect(DiagnosticLog::Open({}) == nullptr,
        L"an omitted diagnostic path should keep logging fully disabled");

    TemporaryDirectory temporary;
    const auto path = temporary.Path() / L"logs" / L"indexer.log";
    auto log = DiagnosticLog::Open(path);
    Expect(log != nullptr, L"an explicit diagnostic path should create a bounded log sink");
    if (!log) return;
    log->Write(L"scan_error", L"path=C:\\Résumé\nerror=5");
    log.reset();

    std::ifstream input(path, std::ios::binary);
    const std::string contents{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    Expect(contents.find("\"event\":\"scan_error\"") != std::string::npos &&
        contents.find("Résumé") != std::string::npos &&
        contents.find("\\\\") != std::string::npos &&
        contents.find("\\n") != std::string::npos,
        L"diagnostic JSONL should preserve UTF-8 and escape paths and control characters");
}

void TestDiagnosticLogRuntimeRotation() {
    using luvletter::indexer::DiagnosticLog;
    TemporaryDirectory temporary;
    const auto path = temporary.Path() / L"runtime.log";
    auto previous = path;
    previous += L".previous";
    constexpr std::uintmax_t maximumBytes = 4U * 1024U * 1024U;
    Expect(CreateEmptyFile(path), L"runtime rotation fixture should be created");
    std::filesystem::resize_file(path, maximumBytes - 64);
    auto log = DiagnosticLog::Open(path);
    Expect(log != nullptr && !std::filesystem::exists(previous),
        L"opening a log below the limit must not rotate it yet");
    if (!log) return;
    log->Write(L"runtime_rotation", std::wstring(512, L'x'));
    log->Write(L"after_rotation", L"diagnostics should keep accepting events");
    log.reset();
    Expect(std::filesystem::exists(previous),
        L"crossing the size limit during Write must rotate the active log");
    if (std::filesystem::exists(previous)) {
        Expect(std::filesystem::file_size(previous) == maximumBytes - 64,
            L"runtime rotation must preserve the previous log before the triggering event");
    }
    Expect(std::filesystem::file_size(path) < maximumBytes,
        L"the new active log must stay within the configured bound");
    std::ifstream input(path, std::ios::binary);
    const std::string contents{std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    Expect(contents.find("runtime_rotation") != std::string::npos &&
        contents.find("after_rotation") != std::string::npos,
        L"the triggering event and following events must survive runtime rotation");
}

void TestIndexPartitionRoutingAndScheduling() {
    using namespace luvletter::indexer;
    using Clock = std::chrono::steady_clock;
    Expect(IsValidPartitionId("filesystem:user-profile") && !IsValidPartitionId("Bad/ID"),
        L"partition IDs must use the stable canonical grammar");
    const std::array partitions{
        IndexPartitionDescriptor{"filesystem:user-profile", LR"(C:\Users\Owner)", {LR"(C:\Users\Owner\Desktop)"},
            PartitionMaintenanceTier::Normal, std::chrono::minutes(30), std::chrono::minutes(1)},
        IndexPartitionDescriptor{"filesystem:desktop", LR"(C:\Users\Owner\Desktop)", {},
            PartitionMaintenanceTier::StartupCritical, std::chrono::minutes(6), std::chrono::minutes(1)},
    };
    const auto* nested = ResolvePartitionOwner(partitions, LR"(c:\USERS\owner\desktop\note.txt)");
    const auto* remainder = ResolvePartitionOwner(partitions, LR"(C:\Users\Owner\Documents\note.txt)");
    Expect(nested != nullptr && nested->id == "filesystem:desktop",
        L"the longest nested root must receive a filesystem event");
    Expect(remainder != nullptr && remainder->id == "filesystem:user-profile",
        L"the parent partition must retain non-delegated paths");
    Expect(ResolvePartitionOwner(partitions, LR"(C:\Users\OwnerElse\note.txt)") == nullptr,
        L"partition roots must match complete path components");
    Expect(ValidatePartitionTopology(partitions),
        L"nested partitions explicitly delegated by every ancestor must be valid");
    auto overlapping = partitions;
    overlapping[0].delegatedSubtrees.clear();
    Expect(!ValidatePartitionTopology(overlapping),
        L"a nested partition without an ancestor scan exclusion must be rejected");
    const auto now = Clock::time_point{} + std::chrono::hours(4);
    const PartitionSchedulingState normal{&partitions[0], now - std::chrono::hours(1),
        now - std::chrono::minutes(10), now - std::chrono::hours(1), std::chrono::minutes(20)};
    const PartitionSchedulingState critical{&partitions[1], now, now, now, std::chrono::minutes(20)};
    Expect(std::isfinite(PartitionSchedulingPriority(normal, now)) &&
        PartitionSchedulingPriority(critical, now) > PartitionSchedulingPriority(normal, now),
        L"scheduler priority must remain finite and honor startup-critical partitions");
    Expect(PartitionCacheName(partitions[0].id) == PartitionCacheName(partitions[0].id) &&
        PartitionCacheName(partitions[0].id) != PartitionCacheName(partitions[1].id),
        L"partition cache names must be stable and isolated");
}

void TestIndexRebuildPolicy() {
    using luvletter::indexer::IndexRebuildPolicy;
    using std::chrono::seconds;
    const std::filesystem::path root = LR"(C:\LuvLetter.Policy.Tests)";
    const auto now = IndexRebuildPolicy::Clock::time_point{} + seconds(100);
    IndexRebuildPolicy policy;
    policy.Configure({root / L"ignored"}, seconds(60));

    Expect(!policy.Accept(root / L"ignored", now),
        L"an ignored directory itself must not request a rebuild");
    Expect(!policy.Accept(root / L"IGNORED" / L"nested" / L"file.tmp", now),
        L"ignore scopes must cover descendants case-insensitively");
    Expect(policy.Accept(root / L"ignored-sibling" / L"file.tmp", now),
        L"ignore scopes must not match a sibling with the same name prefix");

    const auto first = root / L"first.txt";
    const auto second = root / L"second.txt";
    Expect(policy.Accept(first, now), L"a new triggering path should be accepted");
    Expect(!policy.Accept(LR"(c:\luvletter.policy.tests\FIRST.TXT)", now + seconds(30)),
        L"case-equivalent triggering paths must share the same cooldown entry");
    Expect(!policy.Accept(first, now + seconds(59)),
        L"a path must remain suppressed until its full cooldown expires");
    Expect(policy.Accept(second, now + seconds(59)),
        L"a different triggering path must have an independent cooldown");
    Expect(policy.Accept(first, now + seconds(60)),
        L"suppressed events must not extend the original 60-second deadline");
    Expect(!policy.Accept(second, now + seconds(60)),
        L"expiry of another path must not reset a later cooldown");

    Expect(policy.AcceptUnknown(now + seconds(60)),
        L"an unattributed watcher event should have its own cooldown entry");
    Expect(!policy.AcceptUnknown(now + seconds(119)),
        L"repeated unattributed watcher events must be suppressed during cooldown");
    Expect(policy.AcceptUnknown(now + seconds(120)),
        L"suppression must not extend an unattributed event's cooldown");
}

void TestIgnoredDeveloperDirectoryNames() {
    using luvletter::indexer::IndexRebuildPolicy;
    using std::chrono::seconds;
    const std::filesystem::path root = LR"(C:\LuvLetter.Policy.Tests)";
    const auto now = IndexRebuildPolicy::Clock::time_point{};
    IndexRebuildPolicy policy;
    policy.Configure({}, seconds(60), {L".git", L"node_modules", L"BIN", L"obj"});

    Expect(!policy.Accept(root / L"repo" / L".git" / L"objects" / L"new-object", now),
        L"source-control directory descendants must not trigger a rebuild");
    Expect(!policy.Accept(root / L"repo" / L"NODE_MODULES" / L"package" / L"index.js", now),
        L"developer directory names must match case-insensitively at any depth");
    Expect(!policy.Accept(root / L"repo" / L"bin", now),
        L"creation of an ignored directory itself must not trigger a rebuild");
    Expect(!policy.Accept(root / L"repo" / L"obj" / L"Debug" / L"generated.cs", now),
        L"nested build output must remain inside the ignored scope");
    Expect(policy.Accept(root / L"repo" / L".github" / L"workflows", now),
        L"directory name rules must not treat .github as .git");
    Expect(policy.Accept(root / L"repo" / L"binoculars" / L"source.cs", now),
        L"directory name rules must not match a longer component prefix");
    Expect(policy.Accept(root / L"repo" / L"file.bin", now) &&
        policy.Accept(root / L"repo" / L"node_modules.txt", now),
        L"directory name rules must not act as filename extension or substring patterns");
    Expect(policy.Accept(LR"(\\bin\share\source.cs)", now),
        L"a UNC server name must not be treated as a directory component");
    Expect(policy.AcceptUnknown(now),
        L"directory name ignores must not suppress unattributed watcher events");
    policy.Configure({}, seconds(60));
    Expect(policy.Accept(root / L"repo" / L"bin", now),
        L"clearing directory name rules must restore change-triggered rebuilds");

    for (const auto name : {L"", L".", L"..", L"*.tmp", L"nested/path", L"nested\\path",
            L"drive:name", L"bad|name", L"bad<name", L"bad>name", L"bad\"name", L"bad?name",
            L"trailing.", L"trailing ", L"control\nname"}) {
        Expect(!IndexRebuildPolicy::IsValidIgnoredDirectoryName(name),
            L"directory name rules must reject paths, patterns, and invalid Windows components");
    }
    Expect(!IndexRebuildPolicy::IsValidIgnoredDirectoryName(std::wstring(256, L'a')) &&
        IndexRebuildPolicy::IsValidIgnoredDirectoryName(L".git"),
        L"directory name rules must enforce component length while permitting dot directories");
}

void TestRebuildDecisionDiagnostics() {
    using luvletter::indexer::IndexRebuildPolicy;
    using luvletter::indexer::RebuildDecision;
    using std::chrono::seconds;
    const std::filesystem::path root = LR"(C:\LuvLetter.Policy.Tests)";
    const auto now = IndexRebuildPolicy::Clock::time_point{};
    IndexRebuildPolicy policy;
    policy.Configure({root / L"ignored"}, seconds(60));
    Expect(policy.Evaluate({}, now).decision == RebuildDecision::InvalidPath &&
        policy.Evaluate(root / L"ignored" / L"file.tmp", now).decision == RebuildDecision::Ignored,
        L"invalid paths and ignored scopes must have distinct diagnostic decisions");
    const auto first = root / L"first.txt";
    Expect(policy.Evaluate(first, now).decision == RebuildDecision::Accepted,
        L"a fresh path should report acceptance");
    const auto refused = policy.Evaluate(first, now + std::chrono::milliseconds(59500));
    Expect(refused.decision == RebuildDecision::Cooldown && refused.remainingCooldownSeconds == seconds(1),
        L"a partially remaining cooldown second must round up for diagnostic output");
    Expect(policy.Evaluate(first, now + seconds(60)).decision == RebuildDecision::Accepted,
        L"a diagnostic refusal must not extend the accepted event's deadline");

    policy.Configure({}, seconds(60));
    bool filled = true;
    for (int index = 0; index < 4096; ++index) {
        filled &= policy.Evaluate(root / std::to_wstring(index), now).decision == RebuildDecision::Accepted;
    }
    const auto capacity = policy.Evaluate(first, now);
    Expect(filled && capacity.decision == RebuildDecision::Capacity &&
        capacity.remainingCooldownSeconds == seconds(0),
        L"a full bounded map must report capacity separately from a path cooldown");
    Expect(policy.Evaluate(root / L"0", now + seconds(1)).decision == RebuildDecision::Cooldown,
        L"an existing path must retain its cooldown diagnosis even when the map is full");
    Expect(policy.EvaluateUnknown(now).decision == RebuildDecision::Capacity &&
        policy.EvaluateUnknown(now + seconds(60)).decision == RebuildDecision::Accepted,
        L"unattributed recovery must report capacity and recover after entries expire");
    const auto unknown = policy.EvaluateUnknown(now + seconds(61));
    Expect(unknown.decision == RebuildDecision::Cooldown && unknown.remainingCooldownSeconds == seconds(59),
        L"unattributed recovery must expose its own remaining cooldown");
}

void TestRebuildIgnorePrecedesCooldown() {
    using luvletter::indexer::IndexRebuildPolicy;
    using luvletter::indexer::RebuildDecision;
    using std::chrono::seconds;
    const std::filesystem::path root = LR"(C:\LuvLetter.Policy.Tests)";
    const auto now = IndexRebuildPolicy::Clock::time_point{};
    IndexRebuildPolicy policy;
    policy.Configure({root / L"ignored"}, seconds(60), {L".codex", L"node_modules"});

    bool allIgnored = true;
    bool allValidAccepted = true;
    for (int index = 0; index < 4096; ++index) {
        const auto name = std::to_wstring(index);
        const auto scoped = policy.Evaluate(root / L"ignored" / name, now);
        const auto developer = policy.Evaluate(root / L"repo" / L".CODEX" / L"sessions" / name, now);
        allIgnored &= scoped.decision == RebuildDecision::Ignored &&
            developer.decision == RebuildDecision::Ignored &&
            scoped.remainingCooldownSeconds == seconds(0) && developer.remainingCooldownSeconds == seconds(0);
        allValidAccepted &= policy.Evaluate(root / L"source" / name, now).decision == RebuildDecision::Accepted;
    }
    Expect(allIgnored && allValidAccepted,
        L"ignored churn must not consume any cooldown slots or block interleaved source changes");
    Expect(policy.Evaluate(root / L"source" / L"extra", now).decision == RebuildDecision::Capacity,
        L"the valid events should fill exactly the bounded cooldown capacity");
    Expect(policy.Evaluate(root / L"ignored" / L"0", now + seconds(30)).decision == RebuildDecision::Ignored &&
        policy.Evaluate(root / L"repo" / L"node_modules" / L"fresh", now + seconds(30)).decision == RebuildDecision::Ignored,
        L"both repeated ignored paths and new ignored paths must stay ignored when the cooldown map is full");
    const auto active = policy.Evaluate(root / L"source" / L"0", now + seconds(30));
    Expect(active.decision == RebuildDecision::Cooldown && active.remainingCooldownSeconds == seconds(30) &&
        policy.Evaluate(root / L"source" / L"0", now + seconds(60)).decision == RebuildDecision::Accepted,
        L"ignored events must not alter a valid path's cooldown or its expiry");
}

void TestIndexBuildQueryAndPersistence() {
    TemporaryDirectory temporary;
    const auto nested = temporary.Path() / L"aaa";
    std::filesystem::create_directories(nested);
    Expect(CreateEmptyFile(nested / L"README.md"), L"README.md test fixture should be created");
    Expect(CreateEmptyFile(nested / L"readme1.md"), L"readme1.md test fixture should be created");
    Expect(CreateEmptyFile(nested / L"Résumé.txt"), L"Unicode test fixture should be created");
    Expect(CreateEmptyFile(nested / L"unrelated.bin"), L"unrelated test fixture should be created");

    const std::array roots{temporary.Path(), nested};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"index should build");
    if (!snapshot) {
        return;
    }
    Expect(snapshot->FileCount() == 4, L"a nested configured root must not duplicate files covered by its parent root");
    Expect(snapshot->EntityCount() == 5, L"child directories should be included as searchable entities");
    Expect(snapshot->MatchesRoots(roots), L"snapshot should retain the normalized configured-roots fingerprint");
    Expect(!snapshot->BaseIdentity().IsEmpty(), L"a newly built base must have a persistent identity");
    Expect(snapshot->AppliedDeltaSequence() == 0,
        L"a full filesystem build must start before any durable Delta sequence");
    const std::array differentRoots{nested};
    Expect(!snapshot->MatchesRoots(differentRoots), L"snapshot must reject a different configured-roots scope");

    const auto directoryMatches = snapshot->Query(L"aa", 5);
    Expect(directoryMatches.size() == 1, L"directory names should be queryable");
    if (directoryMatches.size() == 1) {
        Expect(directoryMatches[0].kind == luvletter::indexing::SearchResultKind::Directory,
            L"directory query result should carry Directory kind");
        Expect(directoryMatches[0].displayName == L"aaa" && directoryMatches[0].fullPath == nested.native(),
            L"directory result should preserve its display name and reconstruct its full path");
    }

    const auto matches = snapshot->Query(L"RE", 5);
    Expect(matches.size() == 2, L"ASCII query should match case-insensitively");
    if (matches.size() == 2) {
        Expect(matches[0].displayName == L"README.md", L"results should have stable filename ordering");
        Expect(matches[1].displayName == L"readme1.md", L"second stable result should be readme1.md");
        Expect(std::filesystem::exists(matches[0].fullPath), L"full path should be reconstructed lazily and correctly");
    }

    const auto unicodeMatches = snapshot->Query(L"rÉ", 5);
    Expect(unicodeMatches.size() == 1 && unicodeMatches[0].displayName == L"Résumé.txt",
        L"Unicode query should match case-insensitively");

    const auto limited = snapshot->Query(L"r", 1);
    Expect(limited.size() == 1, L"query must honor maximum result count");
    const auto originalStableId = limited.empty() ? 0 : limited[0].stableId;

    const auto allResults = snapshot->AllResults();
    Expect(allResults.size() == snapshot->EntityCount(),
        L"maintenance export should include every searchable entity");

    const auto snapshotPath = temporary.Path() / L"cache" / L"file-index-v4.bin";
    Expect(snapshot->Save(snapshotPath), L"snapshot should save atomically");
    const auto restored = luvletter::indexing::IndexSnapshot::Load(snapshotPath);
    Expect(restored != nullptr, L"valid snapshot should load");
    if (restored) {
        const auto restoredMatches = restored->Query(L"r", 1);
        Expect(restoredMatches.size() == 1 && restoredMatches[0].stableId == originalStableId,
            L"restored index should preserve stable ids and query behavior");
        const auto restoredDirectories = restored->Query(L"aaa", 1);
        Expect(restoredDirectories.size() == 1 &&
            restoredDirectories[0].kind == luvletter::indexing::SearchResultKind::Directory,
            L"restored index should preserve entity kinds");
        Expect(restored->MatchesRoots(roots), L"restored index should preserve its roots fingerprint");
        Expect(restored->BaseIdentity() == snapshot->BaseIdentity(),
            L"snapshot v4 should round-trip its base identity");
        Expect(restored->AppliedDeltaSequence() == snapshot->AppliedDeltaSequence(),
            L"snapshot v4 should round-trip its applied Delta sequence");
        Expect(SameResults(restored->AllResults(), allResults),
            L"snapshot v4 should round-trip the complete maintenance export");
    }

    const luvletter::indexing::IndexBaseIdentity compactedIdentity{
        0x1020304050607080ULL,
        0x8877665544332211ULL};
    constexpr std::uint64_t compactedSequence = 73;
    const auto compacted = luvletter::indexing::IndexBuilder::BuildFromResults(
        allResults,
        snapshot->RootsFingerprint(),
        compactedIdentity,
        compactedSequence);
    Expect(compacted != nullptr, L"maintenance results should build a compact replacement base");
    if (compacted) {
        Expect(compacted->BaseIdentity() == compactedIdentity,
            L"compaction should publish the requested new base identity");
        Expect(compacted->AppliedDeltaSequence() == compactedSequence,
            L"compaction should persist the last incorporated Delta sequence");
        Expect(compacted->RootsFingerprint() == snapshot->RootsFingerprint(),
            L"compaction should preserve configured-root provenance");
        Expect(SameResults(compacted->AllResults(), allResults),
            L"building from results should preserve all stable ids, kinds, names, and paths");
        Expect(SameResults(compacted->Query(L"RE", 5), snapshot->Query(L"RE", 5)) &&
            SameResults(compacted->Query(L"aaa", 5), snapshot->Query(L"aaa", 5)) &&
            SameResults(compacted->Query(L"rÉ", 5), snapshot->Query(L"rÉ", 5)),
            L"a compacted base should preserve query and ranking semantics");

        const auto compactedPath = temporary.Path() / L"cache" / L"compacted-v4.bin";
        Expect(compacted->Save(compactedPath), L"a compacted v4 base should save");
        const auto restoredCompacted = luvletter::indexing::IndexSnapshot::Load(compactedPath);
        Expect(restoredCompacted != nullptr &&
            restoredCompacted->BaseIdentity() == compactedIdentity &&
            restoredCompacted->AppliedDeltaSequence() == compactedSequence &&
            SameResults(restoredCompacted->AllResults(), allResults),
            L"a compacted v4 base should round-trip its contents and recovery metadata");
    }

    const HANDLE file = CreateFileW(
        snapshotPath.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        Expect(false, L"snapshot should open for schema-version mutation");
    } else {
        LARGE_INTEGER versionOffset{};
        versionOffset.QuadPart = 4;
        SetFilePointerEx(file, versionOffset, nullptr, FILE_BEGIN);
        const std::array<std::byte, 2> oldVersion{std::byte{3}, std::byte{0}};
        DWORD written = 0;
        Expect(WriteFile(file, oldVersion.data(), static_cast<DWORD>(oldVersion.size()), &written, nullptr) &&
            written == oldVersion.size(), L"snapshot schema version should be mutated");
        CloseHandle(file);
        Expect(luvletter::indexing::IndexSnapshot::Load(snapshotPath) == nullptr,
            L"snapshot schema v3 must not load as the current v4 index");
    }

    Expect(snapshot->Save(snapshotPath), L"snapshot should be restored before checksum mutation");
    const HANDLE checksumFile = CreateFileW(
        snapshotPath.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (checksumFile == INVALID_HANDLE_VALUE) {
        Expect(false, L"snapshot should open for checksum mutation");
    } else {
        LARGE_INTEGER endOffset{};
        endOffset.QuadPart = -1;
        SetFilePointerEx(checksumFile, endOffset, nullptr, FILE_END);
        std::byte value{};
        DWORD read = 0;
        Expect(ReadFile(checksumFile, &value, 1, &read, nullptr) && read == 1,
            L"snapshot payload byte should be readable");
        endOffset.QuadPart = -1;
        SetFilePointerEx(checksumFile, endOffset, nullptr, FILE_END);
        value ^= std::byte{0x01};
        DWORD checksumWritten = 0;
        Expect(WriteFile(checksumFile, &value, 1, &checksumWritten, nullptr) && checksumWritten == 1,
            L"snapshot payload byte should be mutated");
        CloseHandle(checksumFile);
        Expect(luvletter::indexing::IndexSnapshot::Load(snapshotPath) == nullptr,
            L"snapshot payload checksum mismatch must be rejected");
    }
}

void TestFullIgnorePathMatching() {
    using luvletter::indexing::PathExclusions;
    const std::array paths{
        std::filesystem::path(LR"(C:\LuvLetter.Exclusions\Private\)"),
        std::filesystem::path(LR"(C:\LuvLetter.Exclusions\secret.txt)"),
        std::filesystem::path(LR"(\\server\share\hidden)")};
    const PathExclusions exclusions(paths);
    Expect(exclusions.Contains(LR"(c:\luvletter.exclusions\PRIVATE)"),
        L"full ignore must cover an exact directory case-insensitively");
    Expect(exclusions.Contains(LR"(C:/LuvLetter.Exclusions/Private/nested/../child.txt)"),
        L"full ignore must normalize separators and dot segments for descendants");
    Expect(exclusions.Contains(LR"(C:\LuvLetter.Exclusions\SECRET.TXT)"),
        L"full ignore must cover an exact file without requiring it to exist");
    Expect(!exclusions.Contains(LR"(C:\LuvLetter.Exclusions\secret.txt.backup)") &&
        !exclusions.Contains(LR"(C:\LuvLetter.Exclusions\Private-sibling\child.txt)"),
        L"full ignore must require path boundaries instead of filename prefixes");
    Expect(!exclusions.Contains(LR"(C:\LuvLetter.Exclusions\Private\..\visible.txt)"),
        L"lexical traversal outside an excluded scope must remain visible");
    Expect(exclusions.Contains(LR"(\\?\C:\LuvLetter.Exclusions\Private\child.txt)") &&
        exclusions.Contains(LR"(\\?\UNC\SERVER\SHARE\hidden\child.txt)"),
        L"extended drive and UNC paths must match conventional exclusion paths");
    Expect(!exclusions.Contains(LR"(\\server\share-other\hidden\child.txt)"),
        L"UNC exclusions must preserve the share boundary");

    const std::array extendedPaths{
        std::filesystem::path(LR"(\\?\C:\LuvLetter.Exclusions\Private\)"),
        std::filesystem::path(LR"(\\?\UNC\server\share\hidden\)")};
    const PathExclusions extended(extendedPaths);
    Expect(extended.Contains(LR"(C:\LuvLetter.Exclusions\Private\child.txt)") &&
        extended.Contains(LR"(\\server\share\hidden\child.txt)"),
        L"extended exclusion paths must also match conventional event paths");

    const std::array driveRoot{std::filesystem::path(LR"(C:\)")};
    const PathExclusions wholeDrive(driveRoot);
    Expect(wholeDrive.Contains(LR"(C:\nested\file.txt)") &&
        !wholeDrive.Contains(LR"(D:\nested\file.txt)"),
        L"excluding a drive root must cover only that drive");
    Expect(!PathExclusions{}.Contains(paths[0]), L"an empty full-ignore list must exclude nothing");
    Expect(exclusions.ContainsNormalized(std::filesystem::path(
            LR"(C:\LuvLetter.Exclusions\Private\child.txt)").lexically_normal()) &&
        !exclusions.ContainsNormalized(std::filesystem::path(
            LR"(C:\LuvLetter.Exclusions\Private-sibling\child.txt)").lexically_normal()),
        L"pre-normalized exclusion matching must retain exact path-boundary semantics");

    const std::array legacyRoots{std::filesystem::path(LR"(C:\LuvLetter.Scope.Tests)")};
    const luvletter::indexing::IndexSnapshot legacy({}, {}, {}, 0x5909BDEE8FABD043ULL, {}, 0);
    Expect(legacy.MatchesRoots(legacyRoots),
        L"empty full ignores must preserve the existing v3 roots-only fingerprint");
}

void TestFullIgnoreBuildAndSnapshotScope() {
    using luvletter::indexing::IndexBuilder;
    using luvletter::indexing::IndexSnapshot;
    TemporaryDirectory temporary;
    const auto privateDirectory = temporary.Path() / L"private";
    const auto siblingDirectory = temporary.Path() / L"private-sibling";
    const auto excludedFile = temporary.Path() / L"secret.txt";
    std::filesystem::create_directories(privateDirectory / L"nested");
    std::filesystem::create_directories(siblingDirectory);
    Expect(CreateEmptyFile(privateDirectory / L"nested" / L"private-child.txt"),
        L"excluded descendant fixture should be created");
    Expect(CreateEmptyFile(siblingDirectory / L"public.txt"),
        L"visible sibling fixture should be created");
    Expect(CreateEmptyFile(excludedFile), L"exact excluded file fixture should be created");
    Expect(CreateEmptyFile(temporary.Path() / L"secret.txt.backup"),
        L"excluded filename prefix sibling fixture should be created");

    // An explicitly configured nested root must not bypass its full ignore.
    const std::array roots{temporary.Path(), privateDirectory};
    const std::array exclusions{privateDirectory, excludedFile};
    const auto unfiltered = IndexBuilder::Build(roots);
    const auto snapshot = IndexBuilder::Build(roots, nullptr, exclusions);
    Expect(unfiltered != nullptr && snapshot != nullptr,
        L"both unfiltered and full-ignore index scopes should build");
    if (!unfiltered || !snapshot) {
        return;
    }
    Expect(snapshot->FileCount() == 2 && snapshot->EntityCount() == 3 && snapshot->DirectoryCount() == 2,
        L"full ignore must omit excluded directory records and every descendant entity");
    Expect(snapshot->Query(L"private-child", 5).empty() && snapshot->Query(L"nested", 5).empty(),
        L"excluded descendants must never enter a searchable snapshot");
    const auto privateMatches = snapshot->Query(L"private", 5);
    Expect(privateMatches.size() == 1 && privateMatches[0].fullPath == siblingDirectory.native(),
        L"an excluded directory must disappear while its filename-prefix sibling stays searchable");
    const auto secretMatches = snapshot->Query(L"secret.txt", 5);
    Expect(secretMatches.size() == 1 && secretMatches[0].displayName == L"secret.txt.backup",
        L"excluding an exact file must retain longer filename-prefix matches");
    Expect(unfiltered->MatchesRoots(roots) && !unfiltered->MatchesRoots(roots, exclusions),
        L"an existing unfiltered cache must be rejected when full ignores are configured");
    Expect(snapshot->MatchesRoots(roots, exclusions) && !snapshot->MatchesRoots(roots),
        L"a filtered cache must match only the exclusion scope that produced it");
    const std::array differentExclusions{privateDirectory};
    Expect(!snapshot->MatchesRoots(roots, differentExclusions),
        L"removing even one full ignore must invalidate the prior cache scope");

    const std::array equivalentExclusions{
        std::filesystem::path(L"\\\\?\\" + excludedFile.native()),
        temporary.Path() / L"PRIVATE" / L".",
        privateDirectory / L""};
    Expect(snapshot->MatchesRoots(roots, equivalentExclusions),
        L"cache scope must normalize exclusion ordering, duplicates, case, and extended prefixes");

    const auto snapshotPath = temporary.Path() / L"filtered-cache.bin";
    Expect(snapshot->Save(snapshotPath), L"filtered snapshot should save");
    const auto restored = IndexSnapshot::Load(snapshotPath);
    Expect(restored != nullptr && restored->MatchesRoots(roots, exclusions) &&
        !restored->MatchesRoots(roots) && restored->Query(L"private-child", 5).empty(),
        L"persisted snapshots must retain their exclusion-aware scope and filtered contents");

    const auto unavailableRoot = temporary.Path() / L"not-created";
    const std::array unavailableRoots{unavailableRoot, unavailableRoot / L"nested"};
    const std::array excludedRoots{unavailableRoot};
    const auto empty = IndexBuilder::Build(unavailableRoots, nullptr, excludedRoots);
    Expect(empty != nullptr && empty->EntityCount() == 0 && empty->DirectoryCount() == 0 &&
        empty->MatchesRoots(unavailableRoots, excludedRoots),
        L"fully excluded roots must produce a valid empty scope even when those roots do not exist");
}

void TestLiveDeltaOrderingAndDirectoryTombstones() {
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;
    using luvletter::indexer::LiveIndexDelta;

    TemporaryDirectory temporary;
    const auto directory = temporary.Path() / L"live";
    const auto child = directory / L"child.txt";
    std::filesystem::create_directories(directory);
    Expect(CreateEmptyFile(child), L"live Delta child fixture should be created");

    LiveIndexDelta delta;
    const std::array addChild{FileSystemChange{child, FileSystemChangeAction::Upsert}};
    Expect(!delta.Apply(addChild), L"one file upsert should remain below the rebuild threshold");
    auto matches = delta.Merge(L"child", {}, 5);
    Expect(matches.size() == 1 && matches[0].fullPath == child.native(),
        L"a live file upsert should be searchable without rebuilding the base");

    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
    const std::array removeDirectory{
        FileSystemChange{directory, FileSystemChangeAction::Remove}};
    Expect(!delta.Apply(removeDirectory), L"one directory tombstone should remain bounded");
    matches = delta.Merge(L"child", {}, 5);
    Expect(matches.empty(), L"a newer directory tombstone must hide older descendant upserts");

    std::filesystem::create_directories(directory);
    Expect(CreateEmptyFile(child), L"recreated live Delta child fixture should be created");
    Expect(!delta.Apply(addChild), L"a recreated child should remain below the rebuild threshold");
    matches = delta.Merge(L"child", {}, 5);
    Expect(matches.size() == 1,
        L"a descendant upsert newer than its ancestor tombstone must become visible");

    const std::array rankedPaths{
        temporary.Path() / L"rank-z.txt",
        temporary.Path() / L"rank.md",
        temporary.Path() / L"rank"};
    std::vector<FileSystemChange> rankedChanges;
    for (const auto& path : rankedPaths) {
        Expect(CreateEmptyFile(path), L"bounded Delta ranking fixture should be created");
        rankedChanges.push_back(FileSystemChange{path, FileSystemChangeAction::Upsert});
    }
    Expect(!delta.Apply(rankedChanges), L"a small Delta batch should remain below the rebuild threshold");
    const auto ranked = delta.Merge(L"rank", {}, 2);
    Expect(ranked.size() == 2 && ranked[0].displayName == L"rank" && ranked[1].displayName == L"rank.md",
        L"bounded Delta merging must retain exact-name and exact-stem ranking");
}

void TestDeltaFilteredBaseQueryFillsTopK() {
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;
    using luvletter::indexer::LiveIndexDelta;

    TemporaryDirectory temporary;
    const auto hiddenDirectory = temporary.Path() / L"hidden";
    const auto visibleDirectory = temporary.Path() / L"visible";
    std::filesystem::create_directories(hiddenDirectory);
    std::filesystem::create_directories(visibleDirectory);

    constexpr int hiddenCandidateCount = 96;
    for (int index = 0; index < hiddenCandidateCount; ++index) {
        auto suffix = std::to_wstring(index);
        suffix.insert(suffix.begin(), 2 - suffix.size(), L'0');
        Expect(CreateEmptyFile(hiddenDirectory / (L"candidate!" + suffix)),
            L"hidden prefix candidate fixture should be created");
    }
    constexpr int visibleCandidateCount = 5;
    for (int index = 0; index < visibleCandidateCount; ++index) {
        auto suffix = std::to_wstring(index);
        Expect(CreateEmptyFile(visibleDirectory / (L"candidate~" + suffix)),
            L"visible prefix candidate fixture should be created");
    }

    const std::array roots{temporary.Path()};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"Delta refill base snapshot should build");
    if (!snapshot) {
        return;
    }

    std::error_code ignored;
    std::filesystem::remove_all(hiddenDirectory, ignored);
    LiveIndexDelta delta;
    const std::array removal{
        FileSystemChange{hiddenDirectory, FileSystemChangeAction::Remove}};
    Expect(!delta.Apply(removal), L"one directory removal should remain within the Delta bound");

    const auto results = delta.Query(L"candidate", *snapshot, visibleCandidateCount);
    Expect(results.size() == visibleCandidateCount,
        L"Delta-filtered base query should refill Top-K beyond all hidden leading candidates");
    Expect(std::all_of(results.begin(), results.end(), [&](const auto& result) {
        return std::filesystem::path(result.fullPath).parent_path() == visibleDirectory;
    }), L"directory-prefix tombstones must not leak hidden base candidates during refill");
}

void TestLiveDeltaRevisionPruning() {
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;
    using luvletter::indexer::LiveIndexDelta;

    TemporaryDirectory temporary;
    const auto first = temporary.Path() / L"first.txt";
    const auto second = temporary.Path() / L"second.txt";
    Expect(CreateEmptyFile(first), L"first revision fixture should be created");
    Expect(CreateEmptyFile(second), L"second revision fixture should be created");

    LiveIndexDelta delta;
    const std::array addFirst{FileSystemChange{first, FileSystemChangeAction::Upsert}};
    const std::array addSecond{FileSystemChange{second, FileSystemChangeAction::Upsert}};
    (void)delta.Apply(addFirst);
    const auto rebuildCutoff = delta.CaptureRevision();
    (void)delta.Apply(addSecond);
    delta.PruneThrough(rebuildCutoff);

    Expect(delta.Merge(L"first", {}, 5).empty(),
        L"a completed rebuild should retire Delta entries through its captured revision");
    const auto remaining = delta.Merge(L"second", {}, 5);
    Expect(remaining.size() == 1 && remaining[0].fullPath == second.native(),
        L"a change newer than the rebuild cutoff must survive pruning");
}

void TestDirectoryChangeMonitorLifecycle() {
    using luvletter::indexer::DirectoryChangeMonitor;
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;
    using luvletter::indexer::LiveIndexDelta;

    TemporaryDirectory temporary;
    std::mutex mutex;
    std::condition_variable changed;
    std::vector<FileSystemChange> observed;
    bool uncertain = false;

    DirectoryChangeMonitor monitor;
    const std::array roots{temporary.Path()};
    const auto excludedDirectory = temporary.Path() / L"index-owned";
    std::filesystem::create_directory(excludedDirectory);
    const std::array exclusions{excludedDirectory};
    monitor.Start(roots, [&](std::vector<FileSystemChange> batch, std::vector<std::uint64_t> uncertainRoots) {
        {
            std::lock_guard lock(mutex);
            observed.insert(
                observed.end(),
                std::make_move_iterator(batch.begin()),
                std::make_move_iterator(batch.end()));
            uncertain |= !uncertainRoots.empty();
        }
        changed.notify_all();
    }, {}, exclusions);

    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    const auto internalFile = excludedDirectory / L"journal.delta";
    Expect(CreateEmptyFile(internalFile), L"excluded watcher fixture should be created");
    std::this_thread::sleep_for(std::chrono::milliseconds(600));
    {
        std::lock_guard lock(mutex);
        Expect(std::none_of(observed.begin(), observed.end(), [&](const auto& item) {
            return item.path.lexically_normal() == internalFile.lexically_normal();
        }), L"watcher publication must ignore index-owned persistence changes");
    }
    const auto original = temporary.Path() / L"watch-old.txt";
    const auto renamed = temporary.Path() / L"watch-new.txt";
    Expect(CreateEmptyFile(original), L"watcher create fixture should be created");

    const auto waitFor = [&](const std::filesystem::path& path, const FileSystemChangeAction action) {
        std::unique_lock lock(mutex);
        return changed.wait_for(lock, std::chrono::seconds(3), [&] {
            return std::any_of(observed.begin(), observed.end(), [&](const auto& item) {
                return item.action == action && item.path.lexically_normal() == path.lexically_normal();
            });
        });
    };
    Expect(waitFor(original, FileSystemChangeAction::UpsertAndReconcile),
        L"ReadDirectoryChangesW should preserve the create-time directory reconciliation hint");
    LiveIndexDelta fileDelta;
    const std::array addFile{
        FileSystemChange{original, FileSystemChangeAction::UpsertAndReconcile}};
    Expect(!fileDelta.Apply(addFile),
        L"a create-time reconciliation hint for an ordinary file must not request a rebuild");

    const auto addedDirectory = temporary.Path() / L"watch-directory";
    std::filesystem::create_directory(addedDirectory);
    Expect(waitFor(addedDirectory, FileSystemChangeAction::UpsertAndReconcile),
        L"ReadDirectoryChangesW should preserve the reconciliation hint for a new directory");
    LiveIndexDelta directoryDelta;
    const std::array addDirectory{
        FileSystemChange{addedDirectory, FileSystemChangeAction::UpsertAndReconcile}};
    Expect(directoryDelta.Apply(addDirectory),
        L"a new directory must request reconciliation so existing descendants cannot be missed");

    std::filesystem::rename(original, renamed);
    Expect(waitFor(original, FileSystemChangeAction::Remove),
        L"ReadDirectoryChangesW should publish the old side of a rename");
    Expect(waitFor(renamed, FileSystemChangeAction::UpsertAndReconcile),
        L"ReadDirectoryChangesW should publish the new side of a rename");

    std::filesystem::remove(renamed);
    Expect(waitFor(renamed, FileSystemChangeAction::Remove),
        L"ReadDirectoryChangesW should publish a file-delete tombstone");
    monitor.Stop();
    Expect(!uncertain, L"ordinary local watcher changes should not require uncertain recovery");
    Expect(std::all_of(observed.begin(), observed.end(), [](const auto& change) {
        return change.rootId != 0;
    }), L"watcher changes should retain their owning root identity");
}

void TestDirectoryChangeMonitorRecovery() {
    using luvletter::indexer::DirectoryChangeMonitor;
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;

    TemporaryDirectory temporary;
    const auto delayedRoot = temporary.Path() / L"delayed-root";
    std::mutex mutex;
    std::condition_variable changed;
    std::vector<FileSystemChange> observed;
    std::size_t uncertainBatches = 0;

    DirectoryChangeMonitor monitor;
    const std::array roots{delayedRoot};
    monitor.Start(roots, [&](std::vector<FileSystemChange> batch, std::vector<std::uint64_t> uncertainRoots) {
        {
            std::lock_guard lock(mutex);
            observed.insert(
                observed.end(),
                std::make_move_iterator(batch.begin()),
                std::make_move_iterator(batch.end()));
            if (!uncertainRoots.empty()) {
                ++uncertainBatches;
            }
        }
        changed.notify_all();
    });

    const auto waitForUncertainBatches = [&](const std::size_t minimum) {
        std::unique_lock lock(mutex);
        return changed.wait_for(lock, std::chrono::seconds(5), [&] {
            return uncertainBatches >= minimum;
        });
    };
    Expect(waitForUncertainBatches(1),
        L"a temporarily unavailable root should publish an uncertain batch");

    std::filesystem::create_directory(delayedRoot);
    Expect(waitForUncertainBatches(2),
        L"a recovered watcher should publish reconciliation after reopening its root");

    const auto recoveredFile = delayedRoot / L"after-recovery.txt";
    Expect(CreateEmptyFile(recoveredFile), L"recovered watcher fixture should be created");
    {
        std::unique_lock lock(mutex);
        Expect(changed.wait_for(lock, std::chrono::seconds(5), [&] {
            return std::any_of(observed.begin(), observed.end(), [&](const auto& item) {
                return item.action == FileSystemChangeAction::UpsertAndReconcile &&
                    item.path.lexically_normal() == recoveredFile.lexically_normal();
            });
        }), L"a watcher should resume publishing changes after its root becomes available");
    }
    monitor.Stop();
}

void TestDeterministicMatchRanking() {
    TemporaryDirectory temporary;
    Expect(CreateEmptyFile(temporary.Path() / L"report"), L"exact-name fixture should be created");
    Expect(CreateEmptyFile(temporary.Path() / L"report.md"), L"exact-stem md fixture should be created");
    Expect(CreateEmptyFile(temporary.Path() / L"report.txt"), L"exact-stem txt fixture should be created");
    Expect(CreateEmptyFile(temporary.Path() / L"reports"), L"short prefix fixture should be created");
    Expect(CreateEmptyFile(temporary.Path() / L"report-old.md"), L"long prefix fixture should be created");

    const std::array roots{temporary.Path()};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"ranking index should build");
    if (!snapshot) {
        return;
    }

    const auto matches = snapshot->Query(L"report", 10);
    Expect(matches.size() == 5, L"ranking query should return all prefix matches");
    if (matches.size() == 5) {
        using luvletter::indexing::ClassifySearchMatch;
        using luvletter::indexing::SearchMatchQuality;
        Expect(matches[0].displayName == L"report" &&
            ClassifySearchMatch(matches[0].displayName, matches[0].kind, L"report") ==
                SearchMatchQuality::ExactName,
            L"exact name should rank first");
        Expect(matches[1].displayName == L"report.md" && matches[2].displayName == L"report.txt" &&
            ClassifySearchMatch(matches[1].displayName, matches[1].kind, L"report") ==
                SearchMatchQuality::ExactStem &&
            ClassifySearchMatch(matches[2].displayName, matches[2].kind, L"report") ==
                SearchMatchQuality::ExactStem,
            L"exact stems should rank before generic prefixes in deterministic order");
        Expect(matches[3].displayName == L"report-old.md" && matches[4].displayName == L"reports",
            L"generic prefix ties should retain deterministic ordinal index ordering");
    }
}

void TestDirectoryWinsAnExactNameTie() {
    TemporaryDirectory temporary;
    const auto fileParent = temporary.Path() / L"file-parent";
    const auto directoryParent = temporary.Path() / L"directory-parent";
    std::filesystem::create_directories(fileParent);
    std::filesystem::create_directories(directoryParent / L"shared");
    Expect(CreateEmptyFile(fileParent / L"shared"), L"same-name file fixture should be created");

    const std::array roots{temporary.Path()};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"same-name kind ranking index should build");
    if (!snapshot) {
        return;
    }

    const auto matches = snapshot->Query(L"shared", 2);
    Expect(matches.size() == 2, L"same-name query should return both filesystem kinds");
    if (matches.size() == 2) {
        Expect(matches[0].kind == luvletter::indexing::SearchResultKind::Directory &&
            matches[1].kind == luvletter::indexing::SearchResultKind::File,
            L"a directory should precede a file when match quality and display name tie");
    }
}

void TestLargePrefixTopKIsNotWindowed() {
    TemporaryDirectory temporary;
    constexpr int genericFixtureCount = 540;
    for (int index = 0; index < genericFixtureCount; ++index) {
        auto suffix = std::to_wstring(index);
        suffix.insert(suffix.begin(), 3 - suffix.size(), L'0');
        const auto name = L"item!" + suffix;
        Expect(CreateEmptyFile(temporary.Path() / name), L"large-prefix fixture should be created");
    }
    Expect(CreateEmptyFile(temporary.Path() / L"item.md"), L"late exact-stem fixture should be created");

    const std::array roots{temporary.Path()};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"large-prefix index should build");
    if (!snapshot) {
        return;
    }

    const auto ranked = snapshot->Query(L"item", 5);
    Expect(ranked.size() == 5, L"large-prefix query should still fill Top-K");
    if (!ranked.empty()) {
        Expect(ranked[0].displayName == L"item.md" &&
            luvletter::indexing::ClassifySearchMatch(ranked[0].displayName, ranked[0].kind, L"item") ==
                luvletter::indexing::SearchMatchQuality::ExactStem,
            L"exact stem after more than 512 ordinal prefixes must still rank first");
    }

    constexpr std::size_t requestedGenericResults = 530;
    const auto generic = snapshot->Query(L"item!", requestedGenericResults);
    Expect(generic.size() == requestedGenericResults,
        L"a request larger than the former scan window should return enough prefix candidates");
    if (generic.size() == requestedGenericResults) {
        Expect(generic.front().displayName == L"item!000" && generic.back().displayName == L"item!529",
            L"large generic Top-K should preserve deterministic ordinal ordering");
    }
}

luvletter::indexer::RecoveryJournalBinding JournalBinding(
    const std::uint64_t high,
    const std::uint64_t low,
    const std::uint64_t roots,
    const std::uint64_t policy,
    const std::uint64_t appliedSequence) {
    return luvletter::indexer::RecoveryJournalBinding{
        luvletter::indexing::IndexBaseIdentity{high, low},
        roots,
        policy,
        appliedSequence};
}

luvletter::indexer::DeltaBatch JournalBatch(
    const std::uint64_t sequence,
    const std::filesystem::path& path,
    const luvletter::indexer::DeltaOperationKind operationKind,
    const luvletter::indexing::SearchResultKind entryKind,
    const std::uint64_t stableId,
    const std::uint64_t rootId = 0) {
    return luvletter::indexer::DeltaBatch{
        sequence,
        {luvletter::indexer::DeltaOperation{
            operationKind,
            entryKind,
            stableId,
            rootId,
            path}}};
}

void TestRecoveryJournalRoundTripAndSequence() {
    using luvletter::indexer::DeltaOperation;
    using luvletter::indexer::DeltaOperationKind;
    using luvletter::indexer::RecoveryJournal;
    using luvletter::indexer::RecoveryJournalOpenStatus;
    using luvletter::indexing::SearchResultKind;

    TemporaryDirectory temporary;
    const auto journalPath = temporary.Path() / L"recovery" / L"delta-v1.log";
    const auto binding = JournalBinding(0x1020304050607080ULL, 0x8877665544332211ULL,
        0xA1A2A3A4ULL, 0xB1B2B3B4ULL, 40);
    auto journal = RecoveryJournal::Create(journalPath, binding);
    Expect(journal != nullptr, L"recovery journal should create its parent directory and header");
    if (!journal) {
        return;
    }
    Expect(journal->NextSequence() == 41,
        L"journal sequence should continue after the base-applied sequence");

    const auto skipped = JournalBatch(
        42, temporary.Path() / L"skip.txt", DeltaOperationKind::Upsert,
        SearchResultKind::File, 10);
    Expect(!journal->AppendAndFlush(skipped),
        L"journal should reject a non-contiguous batch sequence");
    Expect(journal->NextSequence() == 41,
        L"a rejected batch must not advance the journal sequence");

    luvletter::indexer::DeltaBatch first{
        41,
        {
            DeltaOperation{
                DeltaOperationKind::Upsert,
                SearchResultKind::File,
                0x1111,
                0xAAAA,
                temporary.Path() / L"Résumé.txt"},
            DeltaOperation{
                DeltaOperationKind::RemoveTree,
                SearchResultKind::Directory,
                0x2222,
                0xBBBB,
                temporary.Path() / L"old-folder"},
        }};
    Expect(journal->AppendAndFlush(first),
        L"journal should append and flush a valid self-contained batch");
    Expect(journal->AppendAndFlush(JournalBatch(
        42, temporary.Path() / L"next", DeltaOperationKind::Remove,
        SearchResultKind::Directory, 0x3333)),
        L"journal should append the next contiguous batch");
    journal.reset();

    std::vector<luvletter::indexer::DeltaBatch> replay;
    auto status = RecoveryJournalOpenStatus::IoError;
    journal = RecoveryJournal::Open(journalPath, binding, replay, status);
    Expect(status == RecoveryJournalOpenStatus::Ready && journal != nullptr,
        L"a valid flushed journal should reopen for append");
    Expect(replay.size() == 2,
        L"a valid journal should replay every complete batch");
    if (replay.size() == 2) {
        Expect(replay[0].sequence == 41 && replay[0].operations.size() == 2 &&
            replay[0].operations[0].path.filename() == L"Résumé.txt" &&
            replay[0].operations[0].rootId == 0xAAAA &&
            replay[0].operations[1].rootId == 0xBBBB &&
            replay[0].operations[1].kind == DeltaOperationKind::RemoveTree,
            L"journal replay should preserve sequence, root ownership, UTF-16 paths, and operation kinds");
        Expect(replay[1].sequence == 42 &&
            replay[1].operations[0].entryKind == SearchResultKind::Directory,
            L"journal replay should preserve entry kinds");
    }
    if (journal) {
        Expect(journal->NextSequence() == 43,
            L"reopened journal should continue after the last complete batch");
    }
}

void TestRecoveryJournalBindingMismatch() {
    using luvletter::indexer::RecoveryJournal;
    using luvletter::indexer::RecoveryJournalOpenStatus;

    TemporaryDirectory temporary;
    const auto path = temporary.Path() / L"binding.log";
    const auto binding = JournalBinding(1, 2, 3, 4, 5);
    auto journal = RecoveryJournal::Create(path, binding);
    Expect(journal != nullptr, L"binding-mismatch fixture should create");
    journal.reset();

    std::vector<luvletter::indexer::DeltaBatch> replay;
    auto status = RecoveryJournalOpenStatus::Ready;
    const auto mismatchedBase = JournalBinding(1, 99, 3, 4, 5);
    Expect(RecoveryJournal::Open(path, mismatchedBase, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::BindingMismatch,
        L"journal should reject a different immutable base identity");
    const auto mismatchedRoots = JournalBinding(1, 2, 99, 4, 5);
    Expect(RecoveryJournal::Open(path, mismatchedRoots, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::BindingMismatch,
        L"journal should reject a different roots fingerprint");
    const auto mismatchedPolicy = JournalBinding(1, 2, 3, 99, 5);
    Expect(RecoveryJournal::Open(path, mismatchedPolicy, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::BindingMismatch,
        L"journal should reject a different policy fingerprint");
    const auto mismatchedSequence = JournalBinding(1, 2, 3, 4, 99);
    Expect(RecoveryJournal::Open(path, mismatchedSequence, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::BindingMismatch,
        L"journal should reject a different base-applied Delta sequence");
}

void TestResolvedChangeJournalReplay() {
    using luvletter::indexer::FileSystemChange;
    using luvletter::indexer::FileSystemChangeAction;
    using luvletter::indexer::LiveIndexDelta;
    using luvletter::indexer::RecoveryJournal;
    using luvletter::indexer::RecoveryJournalOpenStatus;

    TemporaryDirectory temporary;
    const auto candidate = temporary.Path() / L"replay-candidate.txt";
    Expect(CreateEmptyFile(candidate), L"resolved replay fixture should be created");
    const std::array changes{
        FileSystemChange{candidate, FileSystemChangeAction::Upsert, 0xCAFE}};
    const auto resolved = luvletter::indexer::ResolveFileSystemChanges(changes);
    Expect(resolved.operations.size() == 1 &&
        resolved.operations[0].rootId == 0xCAFE &&
        resolved.operations[0].path == candidate,
        L"watcher notifications should resolve into self-contained rooted operations");

    const auto binding = JournalBinding(101, 202, 303, 404, 8);
    const auto journalPath = temporary.Path() / L"resolved-replay.log";
    auto journal = RecoveryJournal::Create(journalPath, binding);
    Expect(journal != nullptr, L"resolved replay journal should create");
    if (!journal) return;
    const luvletter::indexer::DeltaBatch batch{9, resolved.operations};
    Expect(journal->AppendAndFlush(batch),
        L"resolved operation should be durable before its in-memory application");
    journal.reset();

    std::error_code ignored;
    std::filesystem::remove(candidate, ignored);
    std::vector<luvletter::indexer::DeltaBatch> replay;
    auto status = RecoveryJournalOpenStatus::IoError;
    journal = RecoveryJournal::Open(journalPath, binding, replay, status);
    Expect(status == RecoveryJournalOpenStatus::Ready && replay.size() == 1,
        L"a complete resolved batch should replay after restart");

    LiveIndexDelta delta;
    delta.Clear(binding.baseAppliedSequence);
    if (!replay.empty()) {
        Expect(!delta.Apply(replay[0].operations, replay[0].sequence),
            L"one replayed operation should remain below the compaction threshold");
    }
    const auto matches = delta.Merge(L"replay-candidate", {}, 5);
    Expect(matches.size() == 1 && matches[0].fullPath == candidate.native(),
        L"journal replay must not depend on the changed file still existing");
}

void TestRecoveryJournalPartialAndCorruptDetection() {
    using luvletter::indexer::DeltaOperationKind;
    using luvletter::indexer::RecoveryJournal;
    using luvletter::indexer::RecoveryJournalOpenStatus;
    using luvletter::indexing::SearchResultKind;

    TemporaryDirectory temporary;
    const auto partialPath = temporary.Path() / L"partial.log";
    const auto binding = JournalBinding(9, 8, 7, 6, 0);
    auto journal = RecoveryJournal::Create(partialPath, binding);
    Expect(journal != nullptr, L"partial-tail fixture should create");
    if (!journal) {
        return;
    }
    Expect(journal->AppendAndFlush(JournalBatch(
        1, temporary.Path() / L"complete.txt", DeltaOperationKind::Upsert,
        SearchResultKind::File, 1)),
        L"complete batch before partial tail should append");
    Expect(journal->AppendAndFlush(JournalBatch(
        2, temporary.Path() / L"partial.txt", DeltaOperationKind::Upsert,
        SearchResultKind::File, 2)),
        L"batch selected for truncation should append");
    journal.reset();
    Expect(TruncateLastByte(partialPath), L"journal tail should truncate");

    std::vector<luvletter::indexer::DeltaBatch> replay;
    auto status = RecoveryJournalOpenStatus::Ready;
    Expect(RecoveryJournal::Open(partialPath, binding, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::PartialTail,
        L"a truncated final batch should be classified as a partial tail");
    Expect(replay.size() == 1 && replay[0].sequence == 1,
        L"only complete batches before a partial tail should be replayable");

    const auto corruptPath = temporary.Path() / L"corrupt.log";
    journal = RecoveryJournal::Create(corruptPath, binding);
    Expect(journal != nullptr, L"corrupt-checksum fixture should create");
    if (!journal) {
        return;
    }
    Expect(journal->AppendAndFlush(JournalBatch(
        1, temporary.Path() / L"checksum.txt", DeltaOperationKind::Upsert,
        SearchResultKind::File, 3)),
        L"checksum fixture batch should append");
    journal.reset();
    Expect(FlipLastByte(corruptPath), L"journal payload should be mutated");
    replay.clear();
    Expect(RecoveryJournal::Open(corruptPath, binding, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::Corrupt && replay.empty(),
        L"a checksum mismatch should reject the journal as corrupt");
}

void TestDeltaBatchOrderingAndRemovalCapacity() {
    using namespace luvletter::indexer;
    using luvletter::indexing::SearchResultKind;
    const auto root = std::filesystem::path(LR"(C:\DeltaBatch)");
    const auto child = root / L"batch-child.txt";
    const luvletter::indexing::IndexSnapshot empty;
    const std::array recreate{
        DeltaOperation{DeltaOperationKind::Upsert, SearchResultKind::File, 1, 1, child},
        DeltaOperation{DeltaOperationKind::RemoveTree, SearchResultKind::Directory, 2, 1, root},
        DeltaOperation{DeltaOperationKind::Upsert, SearchResultKind::File, 3, 1, child}};
    LiveIndexDelta delta;
    Expect(!delta.Apply(recreate, 1), L"small ordered operation batch should remain bounded");
    const auto recreated = delta.Query(L"batch-child", empty, 5);
    Expect(recreated.size() == 1 && recreated[0].stableId == 3,
        L"recreation after tree removal within one batch must remain visible");
    const std::array removeAgain{
        DeltaOperation{DeltaOperationKind::Upsert, SearchResultKind::File, 4, 1, child},
        DeltaOperation{DeltaOperationKind::RemoveTree, SearchResultKind::Directory, 2, 1, root}};
    (void)delta.Apply(removeAgain, 2);
    Expect(delta.Query(L"batch-child", empty, 5).empty(),
        L"tree removal must hide an earlier upsert in the same batch");

    LiveIndexDelta replacement;
    replacement.Clear(10);
    (void)replacement.Apply(std::span(recreate).last(1), 11);
    delta.Swap(replacement);
    Expect(delta.CaptureRevision() == 11 && replacement.CaptureRevision() == 2 &&
        delta.Query(L"batch-child", empty, 5).size() == 1 &&
        replacement.Query(L"batch-child", empty, 5).empty(),
        L"prebuilt Delta swap must exchange revision and all visible/tombstone state");
    delta.Swap(delta);
    Expect(delta.CaptureRevision() == 11 && delta.Query(L"batch-child", empty, 5).size() == 1,
        L"a self swap must preserve the prebuilt Delta");
    LiveIndexDelta removals;
    std::vector<DeltaOperation> operations;
    operations.reserve(32768);
    for (std::uint64_t index = 0; index < 32768; ++index) {
        operations.push_back(DeltaOperation{DeltaOperationKind::Remove,
            SearchResultKind::File, index + 1, 1,
            root / (L"removed-" + std::to_wstring(index) + L".txt")});
    }
    std::vector<std::filesystem::path> causes;
    Expect(removals.Apply(operations, 1, &causes),
        L"removal-only batches must request maintenance at the Delta capacity");
    Expect(removals.ChangeCount() == 0 && !causes.empty(),
        L"removal-only batches must observe the hard memory bound and report rebuild causes");
}

void TestRecoveryJournalAtomicReset() {
    using luvletter::indexer::DeltaOperationKind;
    using luvletter::indexer::RecoveryJournal;
    using luvletter::indexer::RecoveryJournalOpenStatus;
    using luvletter::indexing::SearchResultKind;

    TemporaryDirectory temporary;
    const auto path = temporary.Path() / L"reset.log";
    const auto oldBinding = JournalBinding(10, 11, 12, 13, 20);
    auto journal = RecoveryJournal::Create(path, oldBinding);
    Expect(journal != nullptr, L"reset fixture should create");
    if (!journal) {
        return;
    }
    Expect(journal->AppendAndFlush(JournalBatch(
        21, temporary.Path() / L"retired.txt", DeltaOperationKind::Remove,
        SearchResultKind::File, 4)),
        L"retired batch should append before reset");

    const auto newBinding = JournalBinding(30, 31, 32, 33, 90);
    Expect(journal->Reset(newBinding),
        L"journal should atomically reset to a new immutable base");
    Expect(journal->NextSequence() == 91,
        L"reset journal should continue after the new base-applied sequence");
    journal.reset();

    std::vector<luvletter::indexer::DeltaBatch> replay;
    auto status = RecoveryJournalOpenStatus::IoError;
    journal = RecoveryJournal::Open(path, newBinding, replay, status);
    Expect(status == RecoveryJournalOpenStatus::Ready && journal != nullptr && replay.empty(),
        L"reset journal should contain only the new binding and no retired batches");
    journal.reset();
    Expect(RecoveryJournal::Open(path, oldBinding, replay, status) == nullptr &&
        status == RecoveryJournalOpenStatus::BindingMismatch,
        L"reset journal should no longer open against the retired base");
}

} // namespace

int wmain() {
    TestPartitionedRecovery();
    TestProtocolHeaderRoundTrip();
    TestStatusPayloadRoundTrip();
    TestIndexPartitionRoutingAndScheduling();
    TestIndexRebuildPolicy();
    TestIgnoredDeveloperDirectoryNames();
    TestRebuildDecisionDiagnostics();
    TestRebuildIgnorePrecedesCooldown();
    TestIndexBuildProgressAndExclusions();
    TestDiagnosticLogOptInAndEscaping();
    TestDiagnosticLogRuntimeRotation();
    TestIndexBuildQueryAndPersistence();
    TestFullIgnorePathMatching();
    TestFullIgnoreBuildAndSnapshotScope();
    TestLiveDeltaOrderingAndDirectoryTombstones();
    TestDeltaFilteredBaseQueryFillsTopK();
    TestLiveDeltaRevisionPruning();
    TestDirectoryChangeMonitorLifecycle();
    TestDirectoryChangeMonitorRecovery();
    TestDeterministicMatchRanking();
    TestDirectoryWinsAnExactNameTie();
    TestLargePrefixTopKIsNotWindowed();
    TestRecoveryJournalRoundTripAndSequence();
    TestResolvedChangeJournalReplay();
    TestRecoveryJournalBindingMismatch();
    TestRecoveryJournalPartialAndCorruptDetection();
    TestDeltaBatchOrderingAndRemovalCapacity();
    TestRecoveryJournalAtomicReset();
    if (failures == 0) {
        std::wcout << L"All LuvLetter.IndexKernel tests passed.\n";
    }
    return failures == 0 ? 0 : 1;
}
