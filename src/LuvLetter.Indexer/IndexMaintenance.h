#pragma once

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
};

// A bounded, memory-only overlay for changes newer than the immutable base snapshot.
// Sequence numbers let a completed rebuild retire only the changes it was able to see.
class LiveIndexDelta final {
public:
    [[nodiscard]] std::uint64_t CaptureRevision() const noexcept;
    [[nodiscard]] bool Apply(std::span<const FileSystemChange> changes);
    [[nodiscard]] std::vector<indexing::SearchResult> Query(
        std::wstring_view query,
        const indexing::IndexSnapshot& baseSnapshot,
        std::size_t maximumResults) const;
    [[nodiscard]] std::vector<indexing::SearchResult> Merge(
        std::wstring_view query,
        std::span<const indexing::SearchResult> baseResults,
        std::size_t maximumResults) const;
    void PruneThrough(std::uint64_t revision);
    void Clear();

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
    using Callback = std::function<void(std::vector<FileSystemChange>, bool uncertain)>;

    DirectoryChangeMonitor();
    ~DirectoryChangeMonitor();
    DirectoryChangeMonitor(const DirectoryChangeMonitor&) = delete;
    DirectoryChangeMonitor& operator=(const DirectoryChangeMonitor&) = delete;

    void Start(std::span<const std::filesystem::path> roots, Callback callback);
    void Stop() noexcept;

private:
    struct Watch;

    void WatchMain(Watch& watch);
    void PublisherMain();
    void Queue(std::vector<FileSystemChange> changes, bool uncertain);
    void StopCore() noexcept;

    std::atomic_bool stopping_ = false;
    HANDLE stopEvent_ = nullptr;
    std::vector<std::unique_ptr<Watch>> watches_;
    std::thread publisher_;
    std::mutex lifecycleMutex_;

    std::mutex pendingMutex_;
    std::condition_variable pendingChanged_;
    std::vector<FileSystemChange> pending_;
    bool pendingUncertain_ = false;
    Callback callback_;
};

} // namespace luvletter::indexer
