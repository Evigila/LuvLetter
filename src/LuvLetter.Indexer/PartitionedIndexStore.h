#pragma once

#include "IndexMaintenance.h"
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

    PartitionedIndexStore(std::filesystem::path dataDirectory, Log log)
        : dataDirectory_(std::move(dataDirectory)), log_(std::move(log)), worker_([this] { WorkerMain(); }) {}

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
        auto fullIgnore = std::make_shared<const luvletter::indexing::PathExclusions>(fullIgnorePaths);
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
            fullIgnore_ = fullIgnore;
            ++statusGeneration_;
            { std::unique_lock viewLock(viewMutex_); viewPartitions_ = configured; }
            UpdateStatusLocked();
        }
        try {
            monitor_.Start(watcherRoots,
                [this](std::vector<FileSystemChange> changes, bool uncertain) {
                    ApplyChanges(std::move(changes), uncertain);
                }, [fullIgnore](const auto& path) { return fullIgnore->Contains(path); });
        } catch (...) { RequestRefresh(std::nullopt, false, WatcherRecovery); }
        for (const auto& partition : configured) LogPartition(*partition, "startup", "Startup rebuild queued");
        changed_.notify_all();
        return true;
    }

    std::vector<luvletter::indexing::SearchResult> Query(
        const std::wstring_view query, const std::size_t maximumResults) const {
        std::vector<luvletter::indexing::SearchResult> merged;
        std::shared_lock viewLock(viewMutex_);
        for (const auto& partition : viewPartitions_) {
            auto found = partition->delta.Query(query, *partition->snapshot, maximumResults);
            for (auto& candidate : found) {
                const auto duplicate = std::find_if(merged.begin(), merged.end(), [&](const auto& item) {
                    return CompareStringOrdinal(item.fullPath.c_str(), -1, candidate.fullPath.c_str(), -1, TRUE) == CSTR_EQUAL;
                });
                if (duplicate == merged.end()) merged.push_back(std::move(candidate));
                else if (luvletter::indexing::IsBetterSearchResult(candidate, *duplicate, query)) *duplicate = std::move(candidate);
            }
        }
        std::sort(merged.begin(), merged.end(), [&](const auto& left, const auto& right) {
            return luvletter::indexing::IsBetterSearchResult(left, right, query);
        });
        if (merged.size() > maximumResults) merged.resize(maximumResults);
        return merged;
    }

    luvletter::indexing::protocol::IndexStatus Status() const noexcept {
        const auto packed = status_.load(std::memory_order_acquire);
        return {packed >> 2U, static_cast<luvletter::indexing::protocol::IndexActivity>(packed & 3U)};
    }

    void RequestRefresh() { RequestRefresh(std::nullopt, true, Forced); }

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
            LogPartition(*partition, force ? "force" : "watcher-recovery", force
                ? "Force rebuild queued | cooldown=bypassed" : "Watcher recovery queued");
        }
        UpdateStatusLocked();
        changed_.notify_all();
    }

    using Clock = std::chrono::steady_clock;
    enum Cause : std::uint32_t { Startup = 1, FileChange = 2, Periodic = 4, Forced = 8,
        WatcherRecovery = 16, Retry = 32 };
    struct Partition final {
        IndexPartitionDescriptor descriptor;
        std::filesystem::path cachePath;
        std::shared_ptr<const luvletter::indexing::IndexSnapshot> snapshot;
        std::shared_ptr<const luvletter::indexing::IndexSnapshot> cached;
        LiveIndexDelta delta;
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

    void ApplyChanges(std::vector<FileSystemChange> changes, const bool uncertain) {
        if (uncertain) RequestRefresh(std::nullopt, false, WatcherRecovery);
        for (auto& change : changes) {
            std::shared_ptr<Partition> owner;
            { std::shared_lock lock(viewMutex_);
                std::size_t best = 0;
                for (const auto& partition : viewPartitions_)
                    if (PartitionContainsPath(partition->descriptor.root, change.path)) {
                        const auto length = NormalizePartitionPath(partition->descriptor.root).size();
                        if (!owner || length > best) { owner = partition; best = length; }
                    }
            }
            if (!owner || fullIgnore_->Contains(change.path)) continue;
            std::vector<std::filesystem::path> causes;
            const std::array batch{change};
            const bool requiresRebuild = owner->delta.Apply(batch, &causes);
            bool accepted = false;
            const auto now = Clock::now();
            for (const auto& path : causes) {
                const auto evaluation = owner->policy.Evaluate(path, now);
                accepted |= evaluation.decision == RebuildDecision::Accepted;
                switch (evaluation.decision) {
                case RebuildDecision::Ignored:
                    LogPartition(*owner, "ignored", "File changed but rebuild ignored");
                    break;
                case RebuildDecision::Cooldown:
                    LogPartition(*owner, "cooldown-refused", "File changed but cooldown refused | remaining_seconds=" +
                        std::to_string(evaluation.remainingCooldownSeconds.count()));
                    break;
                case RebuildDecision::Capacity:
                    LogPartition(*owner, "capacity-refused", "File changed but cooldown capacity refused");
                    break;
                case RebuildDecision::InvalidPath:
                    LogPartition(*owner, "invalid-path", "File changed but path was invalid");
                    break;
                case RebuildDecision::Accepted:
                    break;
                }
            }
            std::lock_guard lock(stateMutex_);
            if (owner->epoch != ownershipEpoch_) continue;
            ++statusGeneration_;
            if (requiresRebuild && accepted) {
                owner->pending = true; owner->causes |= FileChange;
                if (owner->dirtySince == Clock::time_point{}) owner->dirtySince = now;
                LogPartition(*owner, "file-change", "File changed triggered | result=queued-or-coalesced");
            }
            UpdateStatusLocked(); changed_.notify_all();
        }
    }

    void WorkerMain() {
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
                const auto epoch = selected->epoch;
                const auto exclusions = ScanExclusions(*selected);
                lock.unlock();
                std::shared_ptr<const luvletter::indexing::IndexSnapshot> cache;
                try { cache = luvletter::indexing::IndexSnapshot::Load(selected->cachePath); }
                catch (...) { cache.reset(); }
                if (!cache || !cache->MatchesRoots(std::array{selected->descriptor.root}, exclusions)) {
                    auto backup = selected->cachePath; backup += L".bak";
                    try { cache = luvletter::indexing::IndexSnapshot::Load(backup); }
                    catch (...) { cache.reset(); }
                    if (cache && !cache->MatchesRoots(std::array{selected->descriptor.root}, exclusions)) cache.reset();
                }
                lock.lock();
                if (cache && epoch == ownershipEpoch_ && selected->epoch == epoch) {
                    std::unique_lock viewLock(viewMutex_);
                    selected->snapshot = selected->cached = std::move(cache); selected->usable = true;
                    ++statusGeneration_; LogPartition(*selected, "cache", "Partition cache published");
                    UpdateStatusLocked();
                }
                continue;
            }
            selected->pending = false; selected->running = true; selected->forced = false;
            const auto causes = std::exchange(selected->causes, 0U);
            const auto epoch = selected->epoch;
            const auto revision = selected->delta.CaptureRevision();
            const auto exclusions = ScanExclusions(*selected);
            cancelBuild_.store(false);
            UpdateStatusLocked();
            lock.unlock();
            const auto started = Clock::now();
            LogPartition(*selected, "rebuild", "Partition rebuild started | causes=" + std::to_string(causes));
            std::shared_ptr<const luvletter::indexing::IndexSnapshot> rebuilt;
            try {
                rebuilt = luvletter::indexing::IndexBuilder::Build(
                    std::array{selected->descriptor.root}, &cancelBuild_, exclusions);
            } catch (...) {
                rebuilt.reset();
            }
            const auto elapsed = std::chrono::ceil<std::chrono::seconds>(Clock::now() - started);
            lock.lock();
            selected->running = false;
            if (selected->epoch != epoch || ownershipEpoch_ != epoch) continue;
            selected->lastServiced = Clock::now();
            selected->nextAllowed = selected->lastServiced + selected->descriptor.automaticGap;
            selected->estimatedCost = (std::max)(elapsed, std::chrono::seconds(1));
            if (rebuilt) {
                { std::unique_lock viewLock(viewMutex_); selected->snapshot = rebuilt;
                    selected->delta.PruneThrough(revision); }
                selected->usable = true; selected->failed = false;
                if (!selected->pending) selected->dirtySince = {};
                ++selected->generation; ++statusGeneration_;
                const auto previous = selected->cached;
                lock.unlock();
                auto backup = selected->cachePath; backup += L".bak";
                const auto backupValue = previous ? previous : rebuilt;
                bool saved = false;
                try {
                    std::error_code directoryError;
                    std::filesystem::create_directories(selected->cachePath.parent_path(), directoryError);
                    saved = !directoryError && backupValue->Save(backup) && rebuilt->Save(selected->cachePath);
                } catch (...) {
                    saved = false;
                }
                lock.lock();
                if (saved) selected->cached = rebuilt;
                LogPartition(*selected, "rebuild", "Partition rebuild completed | cache=" + std::string(saved ? "saved" : "not-saved"));
            } else {
                selected->failed = true; selected->pending = true; selected->causes |= Retry;
                LogPartition(*selected, "rebuild", "Partition rebuild failed | previous-snapshot-retained");
            }
            UpdateStatusLocked();
        }
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
        status_.store((statusGeneration_ << 2U) | static_cast<std::uint64_t>(activity), std::memory_order_release);
    }

    void LogPartition(const Partition& partition, std::string_view event, const std::string& message) const {
        log_(event, "partition=" + partition.descriptor.id + " | " + message);
    }

    const std::filesystem::path dataDirectory_;
    const Log log_;
    mutable std::shared_mutex viewMutex_;
    std::vector<std::shared_ptr<Partition>> viewPartitions_;
    std::mutex stateMutex_;
    std::condition_variable changed_;
    std::vector<std::shared_ptr<Partition>> partitions_;
    std::shared_ptr<const luvletter::indexing::PathExclusions> fullIgnore_ =
        std::make_shared<const luvletter::indexing::PathExclusions>();
    DirectoryChangeMonitor monitor_;
    std::atomic_bool cancelBuild_ = false;
    std::atomic_uint64_t status_ = 0;
    std::uint64_t statusGeneration_ = 0, ownershipEpoch_ = 0;
    bool stopping_ = false;
    std::thread worker_;
};

} // namespace luvletter::indexer
