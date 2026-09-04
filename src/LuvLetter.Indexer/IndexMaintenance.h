#pragma once

#include "IndexRecovery.h"
#include "luvletter/indexing/FileIndex.h"

#include <Windows.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <condition_variable>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <span>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace luvletter::indexer {

enum class FileSystemChangeAction : std::uint8_t {
    Upsert,
    UpsertAndReconcile,
    Remove,
};

struct FileSystemChange final {
    std::filesystem::path path;
    FileSystemChangeAction action = FileSystemChangeAction::Upsert;
    std::uint64_t rootId = 0;
};

struct ResolvedFileSystemChanges final {
    std::vector<DeltaOperation> operations;
    bool requiresReconciliation = false;
    std::vector<std::filesystem::path> rebuildCauses;
};

// Resolves volatile watcher notifications into self-contained operations before
// they are written to the recovery journal.
[[nodiscard]] ResolvedFileSystemChanges ResolveFileSystemChanges(
    std::span<const FileSystemChange> changes);

// A bounded, memory-only overlay for changes newer than the immutable base snapshot.
// Sequence numbers let a completed rebuild retire only the changes it was able to see.
class LiveIndexDelta final {
public:
    [[nodiscard]] std::uint64_t CaptureRevision() const noexcept;
    [[nodiscard]] bool Apply(
        std::span<const FileSystemChange> changes,
        std::vector<std::filesystem::path>* rebuildCauses = nullptr);
    [[nodiscard]] bool Apply(
        std::span<const DeltaOperation> operations,
        std::uint64_t sequence,
        std::vector<std::filesystem::path>* rebuildCauses = nullptr);
    [[nodiscard]] std::vector<indexing::SearchResult> Query(
        std::wstring_view query,
        const indexing::IndexSnapshot& baseSnapshot,
        std::size_t maximumResults) const;
    [[nodiscard]] std::vector<indexing::SearchResult> Merge(
        std::wstring_view query,
        std::span<const indexing::SearchResult> baseResults,
        std::size_t maximumResults) const;
    void PruneThrough(std::uint64_t revision);
    void Clear(std::uint64_t appliedSequence = 0);
    // Exchange prebuilt views without allocating after the disk commit point.
    void Swap(LiveIndexDelta& other);

    [[nodiscard]] std::size_t ChangeCount() const noexcept;

private:
    struct VersionedResult final {
        indexing::SearchResult result;
        std::uint64_t revision = 0;
    };

    [[nodiscard]] std::vector<indexing::SearchResult> MergeLocked(
        std::wstring_view query,
        std::span<const indexing::SearchResult> baseResults,
        std::size_t maximumResults) const;

    mutable std::shared_mutex mutex_;
    std::unordered_map<std::wstring, VersionedResult> upserts_;
    std::unordered_map<std::wstring, std::uint64_t> tombstones_;
    std::unordered_map<std::wstring, std::uint64_t> removedPrefixes_;
    std::uint64_t revision_ = 0;
    std::uint64_t unsafeRevision_ = 0;
    bool unsafe_ = false;
};

// ReadDirectoryChangesW callbacks are collected off the query path, bounded, and
// published as one batch after a short debounce interval.
class DirectoryChangeMonitor final {
public:
    using Callback = std::function<void(
        std::vector<FileSystemChange>,
        std::vector<std::uint64_t> uncertainRootIds)>;

    DirectoryChangeMonitor();
    ~DirectoryChangeMonitor();
    DirectoryChangeMonitor(const DirectoryChangeMonitor&) = delete;
    DirectoryChangeMonitor& operator=(const DirectoryChangeMonitor&) = delete;

    void Start(
        std::span<const std::filesystem::path> roots,
        Callback callback,
        std::function<bool(const std::filesystem::path&)> excludePath = {},
        std::span<const std::filesystem::path> excludedPaths = {});
    void Stop() noexcept;

private:
    struct Watch;

    void WatchMain(Watch& watch);
    void PublisherMain();
    void Queue(
        std::vector<FileSystemChange> changes,
        std::uint64_t uncertainRootId = 0);
    void StopCore() noexcept;

    std::atomic_bool stopping_ = false;
    HANDLE stopEvent_ = nullptr;
    std::vector<std::unique_ptr<Watch>> watches_;
    std::thread publisher_;
    std::mutex lifecycleMutex_;

    std::mutex pendingMutex_;
    std::condition_variable pendingChanged_;
    std::vector<FileSystemChange> pending_;
    std::unordered_set<std::uint64_t> pendingUncertainRoots_;
    std::vector<std::wstring> excludedPrefixes_;
    Callback callback_;
    std::function<bool(const std::filesystem::path&)> excludePath_;
};

} // namespace luvletter::indexer
