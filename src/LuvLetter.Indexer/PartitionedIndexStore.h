#pragma once

#include "IndexMaintenance.h"
#include "IndexDiagnostics.h"
#include "IndexGeneration.h"
#include "IndexPartitioning.h"
#include "IndexRebuildPolicy.h"
#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"

#include <atomic>
#include <algorithm>
#include <array>
#include <condition_variable>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <shared_mutex>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <unordered_map>
#include <vector>

namespace luvletter::indexer {

class PartitionedIndexStore final {
public:
    using Log = std::function<void(std::string_view, std::string_view)>;

    PartitionedIndexStore(std::filesystem::path dataDirectory, Log log,
        DiagnosticLog* diagnostics = nullptr, std::filesystem::path diagnosticPath = {})
        : dataDirectory_(std::move(dataDirectory)), log_(std::move(log)), diagnostics_(diagnostics),
          diagnosticPath_(std::move(diagnosticPath)), worker_([this] { WorkerMain(); }) {}

    ~PartitionedIndexStore() {
        monitor_.Stop();
        { std::lock_guard lock(stateMutex_); stopping_ = true; cancelBuild_.store(true); }
        changed_.notify_all();
        if (worker_.joinable()) worker_.join();
    }

    PartitionedIndexStore(const PartitionedIndexStore&) = delete;
    PartitionedIndexStore& operator=(const PartitionedIndexStore&) = delete;

    bool Configure(std::vector<IndexPartitionDescriptor> descriptors,
        const std::chrono::seconds triggerCooldown,
        const std::vector<std::filesystem::path>& ignoredDirectories,
        const std::vector<std::wstring>& ignoredNames,
        const std::vector<std::filesystem::path>& fullIgnorePaths) {
        if (!ValidatePartitionTopology(descriptors)) return false;
        auto exclusions = fullIgnorePaths;
        exclusions.push_back(dataDirectory_);
        if (!diagnosticPath_.empty()) {
            exclusions.push_back(diagnosticPath_);
            auto previous = diagnosticPath_; previous += L".previous";
            exclusions.push_back(std::move(previous));
        }
        auto fullIgnore = std::make_shared<const luvletter::indexing::PathExclusions>(exclusions);
        std::vector<std::shared_ptr<Partition>> configured;
        configured.reserve(descriptors.size());
        std::unordered_map<std::string, bool> ids;
        std::unordered_map<std::string, bool> cacheNames;
        for (auto& descriptor : descriptors) {
            if (!IsValidPartitionId(descriptor.id) || descriptor.root.empty() ||
                !descriptor.root.is_absolute() || descriptor.refreshAge < std::chrono::seconds(1) ||
                descriptor.automaticGap < std::chrono::seconds(1) ||
                (descriptor.tier != PartitionMaintenanceTier::StartupCritical &&
                    descriptor.tier != PartitionMaintenanceTier::Normal) ||
                !ids.emplace(descriptor.id, true).second) return false;
            for (const auto& delegated : descriptor.delegatedSubtrees)
                if (!delegated.is_absolute() || !PartitionContainsPath(descriptor.root, delegated) ||
                    SamePartitionPath(descriptor.root, delegated)) return false;
            auto partition = std::make_shared<Partition>();
            partition->descriptor = std::move(descriptor);
            partition->normalizedRoot = NormalizePartitionPath(partition->descriptor.root);
            partition->snapshot = std::make_shared<const luvletter::indexing::IndexSnapshot>();
            partition->policy.Configure(ignoredDirectories, triggerCooldown, ignoredNames);
            partition->pending = true;
            partition->causes = Startup;
            partition->dirtySince = Clock::now();
            partition->nextPeriodic = partition->dirtySince + partition->descriptor.refreshAge;
            const auto cacheName = PartitionCacheName(partition->descriptor.id);
            if (!cacheNames.emplace(cacheName, true).second) return false;
            partition->cachePath = dataDirectory_ / L"partitions" /
                luvletter::indexing::Utf8ToWide(cacheName);
            partition->generationDirectory = partition->cachePath;
            partition->generationDirectory.replace_extension();
            configured.push_back(std::move(partition));
        }
        std::vector<std::filesystem::path> watcherRoots;
        for (const auto& candidate : configured) {
            const bool covered = std::any_of(configured.begin(), configured.end(), [&](const auto& parent) {
                return parent != candidate && PartitionContainsPath(parent->descriptor.root, candidate->descriptor.root);
            });
            if (!covered) watcherRoots.push_back(candidate->descriptor.root);
        }
        monitor_.Stop();
        {
            std::lock_guard stateLock(stateMutex_);
            cancelBuild_.store(true);
            ++ownershipEpoch_;
            for (auto& partition : configured) partition->epoch = ownershipEpoch_;
            partitions_ = configured;
            ++statusGeneration_;
            { std::unique_lock viewLock(viewMutex_);
                viewPartitions_ = configured;
                fullIgnore_ = fullIgnore;
            }
            UpdateStatusLocked();
        }
        try {
            monitor_.Start(watcherRoots,
                [this](std::vector<FileSystemChange> changes, std::vector<std::uint64_t> uncertainRoots) {
                    ApplyChanges(std::move(changes), std::move(uncertainRoots));
                }, [fullIgnore](const auto& path) { return fullIgnore->Contains(path); });
        } catch (...) { RequestRefresh(std::nullopt, false, WatcherRecovery); }
        for (const auto& partition : configured) LogPartition(*partition, "startup", "Startup rebuild queued");
        changed_.notify_all();
        return true;
    }

    std::vector<luvletter::indexing::SearchResult> Query(
        const std::wstring_view query, const std::size_t maximumResults) const {
        std::vector<luvletter::indexing::SearchResult> merged;
        merged.reserve(maximumResults);
        const auto better = [&](const auto& left, const auto& right) {
            return luvletter::indexing::IsBetterSearchResult(left, right, query);
        };
        std::shared_lock viewLock(viewMutex_);
        for (const auto& partition : viewPartitions_) {
            auto found = partition->delta.Query(query, *partition->snapshot, maximumResults);
            for (auto& candidate : found) {
                const auto duplicate = std::find_if(merged.begin(), merged.end(), [&](const auto& item) {
                    return item.stableId == candidate.stableId &&
                        CompareStringOrdinal(item.fullPath.c_str(), -1, candidate.fullPath.c_str(), -1, TRUE) == CSTR_EQUAL;
                });
                if (duplicate != merged.end()) {
                    if (better(candidate, *duplicate)) {
                        *duplicate = std::move(candidate);
                        std::make_heap(merged.begin(), merged.end(), better);
                    }
                } else if (merged.size() < maximumResults) {
                    merged.push_back(std::move(candidate));
                    std::push_heap(merged.begin(), merged.end(), better);
                } else if (!merged.empty() && better(candidate, merged.front())) {
                    std::pop_heap(merged.begin(), merged.end(), better);
                    merged.back() = std::move(candidate);
                    std::push_heap(merged.begin(), merged.end(), better);
                }
            }
        }
        std::sort(merged.begin(), merged.end(), better);
        return merged;
    }

    luvletter::indexing::protocol::IndexStatus Status() const noexcept {
        const auto now = GetTickCount64();
        const auto progressTick = lastProgressTick_.load();
        if (diagnostics_ && progressTick != 0 && now - progressTick >= 30000U &&
            now - lastStallLogTick_.load() >= 30000U) {
            lastStallLogTick_.store(now);
            diagnostics_->Write(L"scan_no_progress", L"No scan progress has been observed for 30 seconds.");
        }
        std::lock_guard lock(statusMutex_);
        return status_;
    }

    void RequestRefresh(const bool force) {
        RequestRefresh(std::nullopt, force, force ? Forced : Manual);
    }

private:
    // Internal target for watcher recovery and retry; null targets all partitions.
    void RequestRefresh(const std::optional<std::string_view> id, const bool force, const std::uint32_t cause) {
        std::lock_guard lock(stateMutex_);
        for (const auto& partition : partitions_) {
            if (id && partition->descriptor.id != *id) continue;
            if (cause == WatcherRecovery && !force) {
                const auto evaluation = partition->policy.EvaluateUnknown(Clock::now());
                if (evaluation.decision != RebuildDecision::Accepted) {
                    LogPartition(*partition, evaluation.decision == RebuildDecision::Cooldown
                        ? "cooldown-refused" : "capacity-refused",
                        "Watcher recovery refused | remaining_seconds=" +
                            std::to_string(evaluation.remainingCooldownSeconds.count()));
                    continue;
                }
            }
            partition->pending = true;
            partition->forced |= force;
            partition->causes |= cause;
            if (partition->dirtySince == Clock::time_point{}) partition->dirtySince = Clock::now();
            const auto eventName = force ? "force" : cause == Manual ? "manual" : "watcher-recovery";
            const auto message = force
                ? "Force rebuild queued | cooldown=bypassed"
                : cause == Manual
                    ? "Reconciliation queued"
                    : "Watcher recovery queued";
            LogPartition(*partition, eventName, message);
        }
        UpdateStatusLocked();
        changed_.notify_all();
    }

    using Clock = std::chrono::steady_clock;
    enum Cause : std::uint32_t { Startup = 1, FileChange = 2, Periodic = 4, Forced = 8,
        WatcherRecovery = 16, Retry = 32, Compaction = 64, Manual = 128 };
    struct Partition final {
        IndexPartitionDescriptor descriptor;
        std::filesystem::path cachePath;
        std::filesystem::path generationDirectory;
        std::wstring normalizedRoot;
        std::shared_ptr<const luvletter::indexing::IndexSnapshot> snapshot;
        std::shared_ptr<const luvletter::indexing::IndexSnapshot> cached;
        LiveIndexDelta delta;
        std::mutex maintenanceMutex;
        std::unique_ptr<RecoveryJournal> journal;
        std::vector<DeltaBatch> batches;
        std::size_t batchBytes = 0;
        bool recoveryGap = false;
        std::uint64_t gapRevision = 0;
        IndexRebuildPolicy policy;
        std::uint64_t epoch = 0;
        std::uint64_t generation = 0;
        bool cacheAttempted = false, usable = false, pending = false, forced = false, running = false, failed = false;
        std::uint32_t causes = 0;
        Clock::time_point dirtySince{}, nextAllowed{}, nextPeriodic{}, lastServiced{};
        std::chrono::seconds estimatedCost{1};
    };

    std::vector<std::filesystem::path> ScanExclusions(const Partition& partition) const {
        std::vector<std::filesystem::path> result(fullIgnore_->Paths().begin(), fullIgnore_->Paths().end());
        result.insert(result.end(), partition.descriptor.delegatedSubtrees.begin(),
            partition.descriptor.delegatedSubtrees.end());
        return result;
    }

    void ApplyChanges(std::vector<FileSystemChange> changes,
        std::vector<std::uint64_t> uncertainRoots) {
        if (!uncertainRoots.empty()) {
            std::vector<std::string> affected;
            {
                std::shared_lock lock(viewMutex_);
                for (const auto& partition : viewPartitions_) {
                    // A watcher may cover several delegated partitions. Recover only its subtree.
                    const auto watcher = std::find_if(viewPartitions_.begin(), viewPartitions_.end(), [&](const auto& p) {
                        const auto id = recovery::StablePathId(recovery::FoldPath(p->descriptor.root));
                        return std::find(uncertainRoots.begin(), uncertainRoots.end(), id) != uncertainRoots.end() &&
                            PartitionContainsPath(p->descriptor.root, partition->descriptor.root);
                    });
                    if (watcher != viewPartitions_.end()) affected.push_back(partition->descriptor.id);
                }
            }
            for (const auto& id : affected) RequestRefresh(id, false, WatcherRecovery);
        }
        struct ChangeGroup final {
            std::shared_ptr<Partition> owner;
            std::vector<FileSystemChange> changes;
        };
        std::vector<ChangeGroup> groups;
        {
            std::shared_lock lock(viewMutex_);
            const auto exclusions = fullIgnore_;
            for (auto& change : changes) {
                auto normalized = NormalizePartitionPath(change.path);
                if (normalized.empty()) continue;
                change.path = std::filesystem::path(std::move(normalized));
                const auto& normalizedText = change.path.native();
                if (!exclusions->Empty() && (normalizedText.starts_with(L"\\\\?\\")
                        ? exclusions->Contains(change.path)
                        : exclusions->ContainsNormalized(change.path))) continue;
                std::shared_ptr<Partition> owner;
                std::size_t best = 0;
                const auto& candidate = change.path.native();
                for (const auto& partition : viewPartitions_) {
                    const auto& root = partition->normalizedRoot;
                    if (candidate.size() < root.size() ||
                        CompareStringOrdinal(root.data(), static_cast<int>(root.size()),
                            candidate.data(), static_cast<int>(root.size()), TRUE) != CSTR_EQUAL ||
                        (candidate.size() != root.size() && root.back() != L'\\' && candidate[root.size()] != L'\\')) continue;
                    if (!owner || root.size() > best) { owner = partition; best = root.size(); }
                }
                if (!owner) continue;
                change.rootId = recovery::StablePathId(recovery::FoldPath(owner->descriptor.root));
                const auto group = std::find_if(groups.begin(), groups.end(), [&](const auto& item) {
                    return item.owner.get() == owner.get();
                });
                if (group == groups.end()) groups.push_back(ChangeGroup{owner, {std::move(change)}});
                else group->changes.push_back(std::move(change));
            }
        }

        struct EvaluationSummary final {
            std::shared_ptr<Partition> owner;
            std::size_t changeCount = 0, accepted = 0, ignored = 0, cooldown = 0, capacity = 0, invalid = 0;
            std::chrono::seconds remainingCooldown{0};
            bool requiresRebuild = false;
            bool persistenceFailed = false, compact = false;
        };
        std::vector<EvaluationSummary> summaries;
        summaries.reserve(groups.size());
        const auto now = Clock::now();
        for (auto& group : groups) {
            std::vector<std::filesystem::path> causes;
            EvaluationSummary summary{group.owner, group.changes.size()};
            auto resolved = ResolveFileSystemChanges(group.changes);
            summary.requiresRebuild = resolved.requiresReconciliation;
            causes = std::move(resolved.rebuildCauses);
            {
                std::lock_guard maintenanceLock(group.owner->maintenanceMutex);
                for (std::size_t offset = 0; offset < resolved.operations.size();) {
                    // A removal expands into two operations. Split watcher batches to
                    // stay within the journal's operation and payload limits.
                    DeltaBatch batch{group.owner->delta.CaptureRevision() + 1U, {}};
                    std::size_t bytes = 0;
                    while (offset < resolved.operations.size()) {
                        const auto& operation = resolved.operations[offset];
                        const bool removalPair = operation.kind == DeltaOperationKind::Remove &&
                            offset + 1U < resolved.operations.size() &&
                            resolved.operations[offset + 1U].kind == DeltaOperationKind::RemoveTree &&
                            resolved.operations[offset + 1U].path == operation.path &&
                            resolved.operations[offset + 1U].rootId == operation.rootId;
                        const std::size_t count = removalPair ? 2U : 1U;
                        if (!batch.operations.empty() && (batch.operations.size() + count > 4096 || bytes >= 1024U * 1024U)) break;
                        for (std::size_t index = 0; index < count; ++index) {
                            auto& next = resolved.operations[offset++];
                            bytes += sizeof(DeltaOperation) + next.path.native().size() * sizeof(wchar_t);
                            batch.operations.push_back(std::move(next));
                        }
                    }
                    // A failed disk or inaccessible root must not turn the recovery
                    // backlog into unbounded resident memory. Preserve the complete
                    // retained prefix and reconcile any notifications beyond this gap.
                    if (group.owner->recoveryGap || group.owner->batchBytes + bytes > 16U * 1024U * 1024U) {
                        group.owner->recoveryGap = true;
                        ++group.owner->gapRevision;
                        group.owner->journal.reset();
                        summary.persistenceFailed = true;
                        break;
                    }
                    if (!group.owner->journal || !group.owner->journal->AppendAndFlush(batch)) {
                        summary.persistenceFailed = true;
                        group.owner->journal.reset();
                    }
                    summary.compact |= group.owner->delta.Apply(batch.operations, batch.sequence);
                    for (const auto& operation : batch.operations)
                        group.owner->batchBytes += sizeof(DeltaOperation) + operation.path.native().size() * sizeof(wchar_t);
                    group.owner->batches.push_back(std::move(batch));
                    summary.compact |= group.owner->batchBytes >= 8U * 1024U * 1024U ||
                        group.owner->batches.size() >= 4096;
                }
            }
            for (const auto& path : causes) {
                const auto evaluation = group.owner->policy.Evaluate(path, now);
                switch (evaluation.decision) {
                case RebuildDecision::Accepted: ++summary.accepted; break;
                case RebuildDecision::Ignored: ++summary.ignored; break;
                case RebuildDecision::Cooldown:
                    ++summary.cooldown;
                    summary.remainingCooldown = (std::max)(summary.remainingCooldown,
                        evaluation.remainingCooldownSeconds);
                    break;
                case RebuildDecision::Capacity: ++summary.capacity; break;
                case RebuildDecision::InvalidPath: ++summary.invalid; break;
                }
            }
            summaries.push_back(std::move(summary));
        }

        bool published = false;
        {
            std::lock_guard lock(stateMutex_);
            for (const auto& summary : summaries) {
                if (summary.owner->epoch != ownershipEpoch_) continue;
                published = true;
                statusGeneration_ += summary.changeCount;
                if (summary.persistenceFailed || summary.compact) {
                    summary.owner->pending = true;
                    summary.owner->causes |= summary.persistenceFailed ? WatcherRecovery : Compaction;
                    if (summary.owner->dirtySince == Clock::time_point{}) summary.owner->dirtySince = now;
                    LogPartition(*summary.owner, summary.persistenceFailed ? "journal-unavailable" : "compaction-queued",
                        summary.persistenceFailed ? "Reconciliation queued; last complete overlay retained" : "Delta compaction queued");
                }
                if (summary.requiresRebuild && summary.accepted != 0) {
                    summary.owner->pending = true;
                    summary.owner->causes |= FileChange;
                    if (summary.owner->dirtySince == Clock::time_point{}) summary.owner->dirtySince = now;
                    LogPartition(*summary.owner, "file-change", "File changed triggered | result=queued-or-coalesced | count=" +
                        std::to_string(summary.accepted));
                }
                if (summary.ignored != 0) LogPartition(*summary.owner, "ignored",
                    "File changed but rebuild ignored | count=" + std::to_string(summary.ignored));
                if (summary.cooldown != 0) LogPartition(*summary.owner, "cooldown-refused",
                    "File changed but cooldown refused | count=" + std::to_string(summary.cooldown) +
                    " | remaining_seconds=" + std::to_string(summary.remainingCooldown.count()));
                if (summary.capacity != 0) LogPartition(*summary.owner, "capacity-refused",
                    "File changed but cooldown capacity refused | count=" + std::to_string(summary.capacity));
                if (summary.invalid != 0) LogPartition(*summary.owner, "invalid-path",
                    "File changed but path was invalid | count=" + std::to_string(summary.invalid));
            }
            if (published) UpdateStatusLocked();
        }
        if (published) changed_.notify_all();
    }

    static RecoveryJournalBinding Binding(const luvletter::indexing::IndexSnapshot& snapshot) {
        return {snapshot.BaseIdentity(), snapshot.RootsFingerprint(),
            recovery::kEnumerationPolicyFingerprint, snapshot.AppliedDeltaSequence()};
    }

    void LoadCache(Partition& partition, const std::vector<std::filesystem::path>& exclusions) {
        // Called with stateMutex_ held. Serialize startup replay with incoming batches.
        std::lock_guard maintenanceLock(partition.maintenanceMutex);
        std::shared_ptr<const luvletter::indexing::IndexSnapshot> cache;
        std::unique_ptr<RecoveryJournal> journal;
        std::vector<DeltaBatch> replay;
        RecoveryJournalOpenStatus recoveryStatus = RecoveryJournalOpenStatus::Missing;
        for (const bool backup : {false, true}) {
            const auto identity = recovery::LoadManifest(partition.generationDirectory, backup);
            if (!identity) continue;
            try { cache = luvletter::indexing::IndexSnapshot::Load(
                recovery::SnapshotPath(partition.generationDirectory, *identity)); }
            catch (...) { cache.reset(); }
            if (!cache || cache->BaseIdentity() != *identity ||
                !cache->MatchesRoots(std::array{partition.descriptor.root}, exclusions)) { cache.reset(); continue; }
            journal = RecoveryJournal::Open(recovery::JournalPath(partition.generationDirectory, *identity),
                Binding(*cache), replay, recoveryStatus);
            break;
        }
        if (!cache) {
            for (const bool backup : {false, true}) {
                auto path = partition.cachePath;
                if (backup) path += L".bak";
                try { cache = luvletter::indexing::IndexSnapshot::Load(path); }
                catch (...) { cache.reset(); }
                if (cache && cache->MatchesRoots(std::array{partition.descriptor.root}, exclusions)) break;
                cache.reset();
            }
        }
        if (!cache) return;
        auto pending = std::move(partition.batches);
        std::unique_lock viewLock(viewMutex_);
        partition.snapshot = partition.cached = cache;
        partition.delta.Clear(cache->AppliedDeltaSequence());
        partition.batches.clear();
        partition.batchBytes = 0;
        const auto rootId = recovery::StablePathId(recovery::FoldPath(partition.descriptor.root));
        const luvletter::indexing::PathExclusions excluded(exclusions);
        for (auto& batch : replay) {
            const bool valid = std::all_of(batch.operations.begin(), batch.operations.end(), [&](const auto& op) {
                return op.rootId == rootId && PartitionContainsPath(partition.descriptor.root, op.path) &&
                    !excluded.Contains(op.path);
            });
            if (!valid) { journal.reset(); recoveryStatus = RecoveryJournalOpenStatus::Corrupt; break; }
            (void)partition.delta.Apply(batch.operations, batch.sequence);
            partition.batches.push_back(std::move(batch));
        }
        // Watcher batches received before cache recovery are newer than the persisted log.
        for (auto& batch : pending) {
            batch.sequence = partition.delta.CaptureRevision() + 1U;
            if (journal && !journal->AppendAndFlush(batch)) journal.reset();
            (void)partition.delta.Apply(batch.operations, batch.sequence);
            partition.batches.push_back(std::move(batch));
        }
        for (const auto& batch : partition.batches)
            for (const auto& operation : batch.operations)
                partition.batchBytes += sizeof(DeltaOperation) + operation.path.native().size() * sizeof(wchar_t);
        partition.journal = std::move(journal);
        partition.usable = true;
        ++statusGeneration_;
        LogPartition(partition, "recovery", "Cache and complete journal batches published | journal_status=" +
            std::to_string(static_cast<int>(recoveryStatus)) + " | batches=" + std::to_string(partition.batches.size()));
    }

    bool ActivateSnapshot(Partition& partition,
        const std::shared_ptr<const luvletter::indexing::IndexSnapshot>& rebuilt,
        const std::uint64_t revision, const std::uint64_t gapRevision, bool& committed) {
        std::lock_guard maintenanceLock(partition.maintenanceMutex);
        // Notifications dropped after the scan cutoff cannot be represented by
        // its retained tail. Only a later complete reconciliation can close that gap.
        if (partition.gapRevision != gapRevision) return false;
        auto journal = RecoveryJournal::Create(
            recovery::JournalPath(partition.generationDirectory, rebuilt->BaseIdentity()), Binding(*rebuilt));
        if (!journal) return false;
        std::vector<DeltaBatch> tail;
        LiveIndexDelta nextDelta;
        nextDelta.Clear(revision);
        std::size_t tailBytes = 0;
        for (const auto& batch : partition.batches) {
            if (batch.sequence <= revision) continue;
            if (!journal->AppendAndFlush(batch)) return false;
            (void)nextDelta.Apply(batch.operations, batch.sequence);
            for (const auto& op : batch.operations)
                tailBytes += sizeof(DeltaOperation) + op.path.native().size() * sizeof(wchar_t);
            tail.push_back(batch);
        }
        const auto obsoleteBackup = recovery::LoadManifest(partition.generationDirectory, true);
        const auto previous = partition.cached;
        if (!recovery::SaveManifest(partition.generationDirectory, rebuilt->BaseIdentity())) return false;
        committed = true;
        {
            std::unique_lock viewLock(viewMutex_);
            partition.snapshot = partition.cached = rebuilt;
            partition.delta.Swap(nextDelta);
        }
        partition.journal = std::move(journal);
        partition.batches = std::move(tail);
        partition.batchBytes = tailBytes;
        partition.recoveryGap = false;
        try {
            if (obsoleteBackup && (!previous || *obsoleteBackup != previous->BaseIdentity()) &&
                *obsoleteBackup != rebuilt->BaseIdentity()) {
                DeleteFileW(recovery::SnapshotPath(partition.generationDirectory, *obsoleteBackup).c_str());
                DeleteFileW(recovery::JournalPath(partition.generationDirectory, *obsoleteBackup).c_str());
            }
        } catch (...) { /* Retaining an older pair is safe if cleanup cannot allocate. */ }
        return true;
    }

    void SetProgress(const luvletter::indexing::protocol::IndexWorkStage stage,
        const std::uint8_t percent, const bool estimated, const std::uint64_t entries) {
        std::lock_guard lock(statusMutex_);
        if (status_.stage == stage && status_.progressPercent != luvletter::indexing::protocol::kUnknownProgress)
            status_.progressPercent = (std::max)(status_.progressPercent, percent);
        else status_.progressPercent = percent;
        status_.stage = stage;
        status_.flags = estimated ? luvletter::indexing::protocol::kEstimatedProgress : 0;
        status_.discoveredEntries = entries;
    }

    void WorkerMain() {
        const bool background = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) != FALSE;
        std::unique_lock lock(stateMutex_);
        while (!stopping_) {
            const auto now = Clock::now();
            for (const auto& p : partitions_) if (now >= p->nextPeriodic) {
                p->pending = true; p->causes |= Periodic; p->dirtySince = now;
                p->nextPeriodic += ((now - p->nextPeriodic) / p->descriptor.refreshAge + 1) * p->descriptor.refreshAge;
                LogPartition(*p, "periodic", "Automatic partition rebuild queued");
            }
            std::shared_ptr<Partition> selected;
            double best = -(std::numeric_limits<double>::max)();
            Clock::time_point wakeAt = (Clock::time_point::max)();
            for (const auto& p : partitions_) {
                wakeAt = (std::min)(wakeAt, p->nextPeriodic);
                if (!p->pending || p->running) continue;
                if (!p->forced && now < p->nextAllowed) { wakeAt = (std::min)(wakeAt, p->nextAllowed); continue; }
                const PartitionSchedulingState state{&p->descriptor, p->nextPeriodic,
                    p->dirtySince, p->lastServiced, p->estimatedCost};
                const auto score = PartitionSchedulingPriority(state, now);
                const bool cacheFirst = selected && !p->cacheAttempted && selected->cacheAttempted;
                const bool sameStage = !selected || p->cacheAttempted == selected->cacheAttempted;
                if (!selected || cacheFirst || sameStage &&
                    (score > best || (score == best && p->descriptor.id < selected->descriptor.id))) {
                    selected = p; best = score;
                }
            }
            if (!selected) {
                if (wakeAt == (Clock::time_point::max)()) changed_.wait(lock);
                else changed_.wait_until(lock, wakeAt);
                continue;
            }
            if (!selected->cacheAttempted) {
                selected->cacheAttempted = true;
                SetProgress(luvletter::indexing::protocol::IndexWorkStage::Recovering, 0, false, 0);
                try { LoadCache(*selected, ScanExclusions(*selected)); }
                catch (...) { LogPartition(*selected, "recovery-failed", "Reconciliation will replace the cache"); }
                UpdateStatusLocked();
                continue;
            }
            selected->pending = false; selected->running = true; selected->forced = false;
            const auto causes = std::exchange(selected->causes, 0U);
            const auto epoch = selected->epoch;
            std::uint64_t revision;
            std::uint64_t gapRevision;
            std::vector<DeltaBatch> captured;
            std::shared_ptr<const luvletter::indexing::IndexSnapshot> base;
            bool compact = false;
            {
                std::lock_guard maintenanceLock(selected->maintenanceMutex);
                compact = causes == Compaction && selected->usable && !selected->recoveryGap;
                revision = selected->delta.CaptureRevision();
                gapRevision = selected->gapRevision;
                if (compact) captured = selected->batches;
                base = selected->snapshot;
            }
            const auto exclusions = ScanExclusions(*selected);
            cancelBuild_.store(false);
            UpdateStatusLocked();
            SetProgress(compact ? luvletter::indexing::protocol::IndexWorkStage::Compacting :
                luvletter::indexing::protocol::IndexWorkStage::Scanning, 0, !compact, 0);
            lock.unlock();
            const auto started = Clock::now();
            lastProgressTick_.store(GetTickCount64());
            lastStallLogTick_.store(0);
            LogPartition(*selected, compact ? "compaction" : "rebuild", "Partition maintenance started | causes=" + std::to_string(causes));
            std::shared_ptr<const luvletter::indexing::IndexSnapshot> rebuilt;
            bool unavailable = false;
            std::uint64_t lastLog = 0;
            std::uint32_t errorLogs = 0;
            try {
                if (compact) {
                    auto results = recovery::ApplyBatches(base->AllResults(), captured, revision);
                    if (!cancelBuild_.load()) rebuilt = luvletter::indexing::IndexBuilder::BuildFromResults(
                        results, base->RootsFingerprint(), luvletter::indexing::CreateIndexBaseIdentity(), revision);
                } else {
                    luvletter::indexing::IndexBuildOptions options;
                    options.appliedDeltaSequence = revision;
                    options.progress = [&](const luvletter::indexing::IndexBuildProgress& progress) {
                        if (cancelBuild_.load()) return;
                        unavailable |= progress.rootUnavailable;
                        if (progress.rootUnavailable) {
                            LogPartition(*selected, "root-unavailable",
                                "Configured root is unavailable | path=" +
                                luvletter::indexing::WideToUtf8(progress.currentPath) +
                                " | error=" + std::to_string(progress.errorCode) +
                                " | previous-snapshot-and-delta=retained");
                        }
                        const auto tick = GetTickCount64();
                        lastProgressTick_.store(tick);
                        if (progress.stage == luvletter::indexing::IndexBuildStage::Packing) {
                            SetProgress(luvletter::indexing::protocol::IndexWorkStage::Packing, 90, false, progress.discoveredEntries);
                        } else {
                            const auto denominator = static_cast<double>(progress.processedDirectories + progress.pendingDirectories + 256U);
                            const auto percent = static_cast<std::uint8_t>((std::min)(89.0,
                                static_cast<double>(progress.processedDirectories) * 90.0 / denominator));
                            SetProgress(luvletter::indexing::protocol::IndexWorkStage::Scanning, percent, true, progress.discoveredEntries);
                        }
                        if (diagnostics_ && ((progress.errorCode != 0 && errorLogs++ < 50U) || tick - lastLog >= 5000U)) {
                            const auto detail = L"partition=" + luvletter::indexing::Utf8ToWide(selected->descriptor.id) +
                                L" entries=" + std::to_wstring(progress.discoveredEntries) + L" directories=" +
                                std::to_wstring(progress.processedDirectories) + L" error=" + std::to_wstring(progress.errorCode) +
                                L" path=" + std::wstring(progress.currentPath);
                            diagnostics_->Write(progress.errorCode != 0 ? L"scan_error" : L"scan_progress", detail);
                            lastLog = tick;
                        }
                    };
                    rebuilt = luvletter::indexing::IndexBuilder::Build(
                        std::array{selected->descriptor.root}, &cancelBuild_, exclusions, std::move(options));
                    // A partition owns one root: retain its whole snapshot AND Delta on failure.
                    // Other partitions can still publish normally, including delegated children.
                    if (unavailable) rebuilt.reset();
                }
                if (rebuilt && !cancelBuild_.load()) {
                    SetProgress(luvletter::indexing::protocol::IndexWorkStage::Persisting, 95, false, rebuilt->EntityCount());
                    std::error_code error;
                    std::filesystem::create_directories(selected->generationDirectory, error);
                    if (error || !rebuilt->Save(recovery::SnapshotPath(selected->generationDirectory, rebuilt->BaseIdentity())))
                        rebuilt.reset();
                } else rebuilt.reset();
            } catch (...) { rebuilt.reset(); }
            lastProgressTick_.store(0);
            const auto elapsed = std::chrono::ceil<std::chrono::seconds>(Clock::now() - started);
            lock.lock();
            selected->running = false;
            if (selected->epoch != epoch || ownershipEpoch_ != epoch) continue;
            selected->lastServiced = Clock::now();
            selected->nextAllowed = selected->lastServiced + selected->descriptor.automaticGap;
            selected->estimatedCost = (std::max)(elapsed, std::chrono::seconds(1));
            bool activated = false;
            bool committed = false;
            try { activated = rebuilt && ActivateSnapshot(*selected, rebuilt, revision, gapRevision, committed); }
            catch (...) { activated = false; }
            if (activated) {
                selected->usable = true; selected->failed = false;
                if (!selected->pending) selected->dirtySince = {};
                ++selected->generation; ++statusGeneration_;
                LogPartition(*selected, compact ? "compaction" : "rebuild", "Partition generation published | snapshot-and-journal=saved");
            } else {
                if (rebuilt && !committed) {
                    DeleteFileW(recovery::SnapshotPath(selected->generationDirectory, rebuilt->BaseIdentity()).c_str());
                    DeleteFileW(recovery::JournalPath(selected->generationDirectory, rebuilt->BaseIdentity()).c_str());
                }
                selected->failed = true; selected->pending = true; selected->causes |= Retry;
                LogPartition(*selected, "rebuild", "Partition maintenance failed | previous-snapshot-and-delta-retained");
            }
            UpdateStatusLocked();
        }
        if (background) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    }

    void UpdateStatusLocked() {
        using Activity = luvletter::indexing::protocol::IndexActivity;
        const bool running = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) { return p->running; });
        const bool queuedWork = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) {
            return p->pending && !p->failed;
        });
        const bool working = running || queuedWork;
        const bool hasCritical = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) {
            return p->descriptor.tier == PartitionMaintenanceTier::StartupCritical;
        });
        const bool hasUsable = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) { return p->usable; });
        const bool criticalMissing = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) {
            return !p->usable && p->descriptor.tier == PartitionMaintenanceTier::StartupCritical;
        });
        const bool initial = hasCritical ? criticalMissing : !hasUsable;
        const bool failed = std::any_of(partitions_.begin(), partitions_.end(), [](const auto& p) { return p->failed; });
        const auto activity = working ? (initial ? Activity::InitialBuild : Activity::Updating)
            : failed ? Activity::Failed : Activity::Ready;
        std::lock_guard statusLock(statusMutex_);
        status_.generation = statusGeneration_;
        status_.activity = activity;
        if (working && (status_.stage == luvletter::indexing::protocol::IndexWorkStage::Idle ||
            status_.progressPercent == 100)) {
            status_.stage = luvletter::indexing::protocol::IndexWorkStage::Recovering;
            status_.progressPercent = luvletter::indexing::protocol::kUnknownProgress;
            status_.flags = 0;
        }
        if (!running) {
            status_.stage = working ? luvletter::indexing::protocol::IndexWorkStage::Recovering :
                luvletter::indexing::protocol::IndexWorkStage::Idle;
            status_.flags = 0;
            status_.progressPercent = activity == Activity::Ready ? 100 : luvletter::indexing::protocol::kUnknownProgress;
        }
    }

    void LogPartition(const Partition& partition, std::string_view event, const std::string& message) const {
        log_(event, "partition=" + partition.descriptor.id + " | " + message);
        if (diagnostics_) diagnostics_->Write(luvletter::indexing::Utf8ToWide(event),
            luvletter::indexing::Utf8ToWide("partition=" + partition.descriptor.id + " | " + message));
    }

    const std::filesystem::path dataDirectory_;
    const Log log_;
    DiagnosticLog* const diagnostics_;
    const std::filesystem::path diagnosticPath_;
    mutable std::shared_mutex viewMutex_;
    std::vector<std::shared_ptr<Partition>> viewPartitions_;
    std::mutex stateMutex_;
    std::condition_variable changed_;
    std::vector<std::shared_ptr<Partition>> partitions_;
    std::shared_ptr<const luvletter::indexing::PathExclusions> fullIgnore_ =
        std::make_shared<const luvletter::indexing::PathExclusions>();
    DirectoryChangeMonitor monitor_;
    std::atomic_bool cancelBuild_ = false;
    mutable std::mutex statusMutex_;
    luvletter::indexing::protocol::IndexStatus status_;
    std::atomic_uint64_t lastProgressTick_ = 0;
    mutable std::atomic_uint64_t lastStallLogTick_ = 0;
    std::uint64_t statusGeneration_ = 0, ownershipEpoch_ = 0;
    bool stopping_ = false;
    std::thread worker_;
};

} // namespace luvletter::indexer
