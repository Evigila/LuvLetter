#include "IndexMaintenance.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cwctype>
#include <limits>
#include <mutex>
#include <optional>
#include <utility>

namespace luvletter::indexer {
namespace {

constexpr std::size_t kMaximumPendingChanges = 4096;
constexpr std::size_t kMaximumDeltaChanges = 8192;
constexpr std::size_t kMaximumRetainedDeltaChanges = 32768;
constexpr auto kPublishDebounce = std::chrono::milliseconds(250);
constexpr DWORD kNotificationBufferBytes = 32U * 1024U;
constexpr DWORD kWatchRetryMilliseconds = 250;

std::filesystem::path NormalizePath(const std::filesystem::path& path) {
    std::error_code error;
    auto absolute = std::filesystem::absolute(path, error);
    if (error) {
        absolute = path;
    }
    return absolute.lexically_normal();
}

std::wstring FoldPath(const std::filesystem::path& path) {
    const auto normalized = NormalizePath(path).native();
    if (normalized.empty()) {
        return {};
    }

    std::wstring folded(normalized.size(), L'\0');
    const int length = LCMapStringEx(
        LOCALE_NAME_INVARIANT,
        LCMAP_LOWERCASE,
        normalized.data(),
        static_cast<int>(normalized.size()),
        folded.data(),
        static_cast<int>(folded.size()),
        nullptr,
        nullptr,
        0);
    if (length == 0) {
        folded.assign(normalized);
        std::transform(folded.begin(), folded.end(), folded.begin(), [](const wchar_t value) {
            return static_cast<wchar_t>(std::towlower(value));
        });
    }
    return folded;
}

std::wstring RemovedPrefix(const std::wstring& foldedPath) {
    if (foldedPath.empty() || foldedPath.back() == L'\\' || foldedPath.back() == L'/') {
        return foldedPath;
    }
    return foldedPath + L'\\';
}

bool IsNewerRevision(const std::uint64_t value, const std::uint64_t reference) noexcept {
    return static_cast<std::int64_t>(value - reference) > 0;
}

bool IsRevisionAtOrBefore(const std::uint64_t value, const std::uint64_t reference) noexcept {
    return !IsNewerRevision(value, reference);
}

std::optional<std::uint64_t> NewestPrefixRemoval(
    const std::unordered_map<std::wstring, std::uint64_t>& removedPrefixes,
    const std::wstring_view key) {
    std::optional<std::uint64_t> newest;
    std::size_t searchBefore = key.size();
    while (searchBefore != 0) {
        const auto separator = key.find_last_of(L"\\/", searchBefore - 1);
        if (separator == std::wstring_view::npos) {
            break;
        }
        std::wstring prefix(key.substr(0, separator + 1));
        const auto removal = removedPrefixes.find(prefix);
        if (removal != removedPrefixes.end() &&
            (!newest.has_value() || IsNewerRevision(removal->second, *newest))) {
            newest = removal->second;
        }
        searchBefore = separator;
    }
    return newest;
}

std::uint64_t StablePathId(const std::wstring_view foldedPath) noexcept {
    constexpr std::uint64_t offsetBasis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    std::uint64_t hash = offsetBasis;
    for (const wchar_t character : foldedPath) {
        const auto value = static_cast<std::uint16_t>(character);
        hash ^= value & 0xFFU;
        hash *= prime;
        hash ^= value >> 8U;
        hash *= prime;
    }
    return hash;
}

std::wstring ExtendedPath(const std::filesystem::path& path) {
    auto value = NormalizePath(path).native();
    if (value.starts_with(L"\\\\?\\")) {
        return value;
    }
    if (value.starts_with(L"\\\\")) {
        return L"\\\\?\\UNC\\" + value.substr(2);
    }
    return L"\\\\?\\" + value;
}

HANDLE OpenDirectoryWatch(const std::filesystem::path& root) {
    const auto extended = ExtendedPath(root);
    return CreateFileW(
        extended.c_str(),
        FILE_LIST_DIRECTORY,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED,
        nullptr);
}

std::optional<indexing::SearchResult> ReadCurrentEntry(const std::filesystem::path& path) {
    const auto normalized = NormalizePath(path);
    const auto extended = ExtendedPath(normalized);
    const DWORD attributes = GetFileAttributesW(extended.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        return std::nullopt;
    }

    const auto kind = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0
        ? indexing::SearchResultKind::Directory
        : indexing::SearchResultKind::File;
    auto name = normalized.filename().native();
    if (name.empty()) {
        name = normalized.native();
    }
    if (name.empty()) {
        return std::nullopt;
    }

    const auto folded = FoldPath(normalized);
    return indexing::SearchResult{
        StablePathId(folded), kind, std::move(name), normalized.native()};
}

} // namespace

std::uint64_t LiveIndexDelta::CaptureRevision() const noexcept {
    std::shared_lock lock(mutex_);
    return revision_;
}

bool LiveIndexDelta::Apply(
    const std::span<const FileSystemChange> changes,
    std::vector<std::filesystem::path>* rebuildCauses) {
    if (changes.empty()) {
        return false;
    }

    std::unique_lock lock(mutex_);
    bool requiresRebuild = unsafe_;
    std::vector<std::filesystem::path> capacityCauses;
    for (const auto& change : changes) {
        const auto normalized = NormalizePath(change.path);
        const auto key = FoldPath(normalized);
        if (key.empty()) {
            continue;
        }

        const auto revision = ++revision_;
        if (unsafe_) {
            unsafeRevision_ = revision;
            requiresRebuild = true;
            if (rebuildCauses != nullptr) {
                rebuildCauses->push_back(normalized);
            }
            continue;
        }
        bool causesRebuild = false;
        if (change.action == FileSystemChangeAction::Remove) {
            upserts_.erase(key);
            tombstones_[key] = revision;
            removedPrefixes_[RemovedPrefix(key)] = revision;
        } else {
            auto current = ReadCurrentEntry(normalized);
            if (!current.has_value()) {
                upserts_.erase(key);
                tombstones_[key] = revision;
                removedPrefixes_[RemovedPrefix(key)] = revision;
            } else {
                tombstones_.erase(key);
                causesRebuild = change.action == FileSystemChangeAction::UpsertAndReconcile &&
                    current->kind == indexing::SearchResultKind::Directory;
                requiresRebuild |= causesRebuild;
                upserts_[key] = VersionedResult{std::move(*current), revision};
            }
        }

        if (upserts_.size() + tombstones_.size() + removedPrefixes_.size()
            >= kMaximumRetainedDeltaChanges) {
            upserts_.clear();
            tombstones_.clear();
            removedPrefixes_.clear();
            unsafe_ = true;
            unsafeRevision_ = revision;
            requiresRebuild = true;
            causesRebuild = true;
        }
        if (rebuildCauses != nullptr) {
            if (causesRebuild) {
                rebuildCauses->push_back(normalized);
            } else if (upserts_.size() + tombstones_.size() + removedPrefixes_.size()
                >= kMaximumDeltaChanges) {
                capacityCauses.push_back(normalized);
            }
        }
    }

    const bool capacityExceeded =
        upserts_.size() + tombstones_.size() + removedPrefixes_.size() >= kMaximumDeltaChanges;
    if (rebuildCauses != nullptr && (capacityExceeded || unsafe_)) {
        rebuildCauses->insert(
            rebuildCauses->end(),
            std::make_move_iterator(capacityCauses.begin()),
            std::make_move_iterator(capacityCauses.end()));
    }
    return requiresRebuild || capacityExceeded;
}

std::vector<indexing::SearchResult> LiveIndexDelta::Query(
    const std::wstring_view query,
    const indexing::IndexSnapshot& baseSnapshot,
    const std::size_t maximumResults) const {
    if (query.empty() || maximumResults == 0) {
        return {};
    }

    std::shared_lock lock(mutex_);
    if (unsafe_) {
        // Keep the last complete snapshot searchable until reconciliation succeeds.
        return baseSnapshot.Query(query, maximumResults);
    }

    const auto baseResults = baseSnapshot.Query(
        query,
        maximumResults,
        [this](const indexing::SearchResult& result) {
            const auto key = FoldPath(result.fullPath);
            return !key.empty() &&
                !tombstones_.contains(key) &&
                !NewestPrefixRemoval(removedPrefixes_, key).has_value() &&
                !upserts_.contains(key);
        });
    return MergeLocked(query, baseResults, maximumResults);
}

std::vector<indexing::SearchResult> LiveIndexDelta::Merge(
    const std::wstring_view query,
    const std::span<const indexing::SearchResult> baseResults,
    const std::size_t maximumResults) const {
    if (query.empty() || maximumResults == 0) {
        return {};
    }

    std::shared_lock lock(mutex_);
    if (unsafe_) {
        const auto retained = (std::min)(baseResults.size(), maximumResults);
        return {baseResults.begin(), baseResults.begin() + retained};
    }
    return MergeLocked(query, baseResults, maximumResults);
}

std::vector<indexing::SearchResult> LiveIndexDelta::MergeLocked(
    const std::wstring_view query,
    const std::span<const indexing::SearchResult> baseResults,
    const std::size_t maximumResults) const {
    std::vector<indexing::SearchResult> merged;

    merged.reserve(baseResults.size() + upserts_.size());
    std::unordered_map<std::wstring, std::size_t> resultByPath;
    resultByPath.reserve(baseResults.size() + upserts_.size());

    for (const auto& result : baseResults) {
        const auto key = FoldPath(result.fullPath);
        if (key.empty() || tombstones_.contains(key) ||
            NewestPrefixRemoval(removedPrefixes_, key).has_value() ||
            upserts_.contains(key)) {
            continue;
        }
        resultByPath.emplace(key, merged.size());
        merged.push_back(result);
    }

    for (const auto& [key, versioned] : upserts_) {
        const auto prefixRemoval = NewestPrefixRemoval(removedPrefixes_, key);
        if (prefixRemoval.has_value() &&
            !IsNewerRevision(versioned.revision, *prefixRemoval)) {
            continue;
        }
        if (indexing::ClassifySearchMatch(
                versioned.result.displayName,
                versioned.result.kind,
                query) == indexing::SearchMatchQuality::None) {
            continue;
        }
        const auto existing = resultByPath.find(key);
        if (existing == resultByPath.end()) {
            resultByPath.emplace(key, merged.size());
            merged.push_back(versioned.result);
        } else {
            merged[existing->second] = versioned.result;
        }
    }

    std::sort(merged.begin(), merged.end(), [&query](const auto& left, const auto& right) {
        return indexing::IsBetterSearchResult(left, right, query);
    });
    if (merged.size() > maximumResults) {
        merged.resize(maximumResults);
    }
    return merged;
}

void LiveIndexDelta::PruneThrough(const std::uint64_t revision) {
    std::unique_lock lock(mutex_);
    std::erase_if(upserts_, [revision](const auto& item) {
        return IsRevisionAtOrBefore(item.second.revision, revision);
    });
    std::erase_if(tombstones_, [revision](const auto& item) {
        return IsRevisionAtOrBefore(item.second, revision);
    });
    std::erase_if(removedPrefixes_, [revision](const auto& item) {
        return IsRevisionAtOrBefore(item.second, revision);
    });
    if (unsafe_ && IsRevisionAtOrBefore(unsafeRevision_, revision)) {
        unsafe_ = false;
        unsafeRevision_ = 0;
    }
}

void LiveIndexDelta::Clear() {
    std::unique_lock lock(mutex_);
    upserts_.clear();
    tombstones_.clear();
    removedPrefixes_.clear();
    unsafe_ = false;
    unsafeRevision_ = 0;
}

std::size_t LiveIndexDelta::ChangeCount() const noexcept {
    std::shared_lock lock(mutex_);
    return upserts_.size() + tombstones_.size() + removedPrefixes_.size();
}

struct DirectoryChangeMonitor::Watch final {
    std::filesystem::path root;
    std::mutex directoryMutex;
    HANDLE directory = INVALID_HANDLE_VALUE;
    std::thread thread;

    [[nodiscard]] HANDLE CurrentDirectory() {
        std::lock_guard lock(directoryMutex);
        return directory;
    }

    void Cancel() noexcept {
        std::lock_guard lock(directoryMutex);
        if (directory != INVALID_HANDLE_VALUE) {
            CancelIoEx(directory, nullptr);
        }
    }

    void ReplaceDirectory(const HANDLE next) noexcept {
        HANDLE previous = INVALID_HANDLE_VALUE;
        {
            std::lock_guard lock(directoryMutex);
            previous = std::exchange(directory, next);
        }
        if (previous != INVALID_HANDLE_VALUE) {
            CloseHandle(previous);
        }
    }

    ~Watch() {
        ReplaceDirectory(INVALID_HANDLE_VALUE);
    }
};

DirectoryChangeMonitor::DirectoryChangeMonitor()
    : stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}

DirectoryChangeMonitor::~DirectoryChangeMonitor() {
    Stop();
    if (stopEvent_ != nullptr) {
        CloseHandle(stopEvent_);
    }
}

void DirectoryChangeMonitor::Start(
    const std::span<const std::filesystem::path> roots,
    Callback callback,
    std::function<bool(const std::filesystem::path&)> excludePath) {
    if (publisher_.joinable() && publisher_.get_id() == std::this_thread::get_id()) {
        return;
    }
    std::lock_guard lifecycleLock(lifecycleMutex_);
    StopCore();
    if (stopEvent_ == nullptr || !callback) {
        return;
    }

    ResetEvent(stopEvent_);
    excludePath_ = std::move(excludePath);
    stopping_.store(false, std::memory_order_release);
    {
        std::lock_guard lock(pendingMutex_);
        callback_ = std::move(callback);
        pending_.clear();
        pendingUncertain_ = false;
    }
    publisher_ = std::thread([this] {
        try {
            PublisherMain();
        } catch (...) {
            stopping_.store(true, std::memory_order_release);
            if (stopEvent_ != nullptr) SetEvent(stopEvent_);
        }
    });

    try {
        watches_.reserve(roots.size());
        for (const auto& root : roots) {
            if (excludePath_ && excludePath_(root)) continue;
            auto watch = std::make_unique<Watch>();
            watch->root = NormalizePath(root);
            watch->directory = OpenDirectoryWatch(watch->root);

            watches_.push_back(std::move(watch));
            auto* watchPointer = watches_.back().get();
            watchPointer->thread = std::thread([this, watchPointer] {
                try {
                    WatchMain(*watchPointer);
                } catch (...) {
                    Queue({}, true);
                }
            });
        }
    } catch (...) {
        StopCore();
        throw;
    }
}

void DirectoryChangeMonitor::Stop() noexcept {
    std::lock_guard lifecycleLock(lifecycleMutex_);
    if (publisher_.joinable() && publisher_.get_id() == std::this_thread::get_id()) {
        stopping_.store(true, std::memory_order_release);
        if (stopEvent_ != nullptr) SetEvent(stopEvent_);
        pendingChanged_.notify_all();
        return;
    }
    StopCore();
}

void DirectoryChangeMonitor::StopCore() noexcept {
    stopping_.store(true, std::memory_order_release);
    if (stopEvent_ != nullptr) {
        SetEvent(stopEvent_);
    }
    pendingChanged_.notify_all();
    for (auto& watch : watches_) {
        watch->Cancel();
    }
    for (auto& watch : watches_) {
        if (watch->thread.joinable()) {
            watch->thread.join();
        }
    }
    watches_.clear();
    if (publisher_.joinable()) {
        publisher_.join();
    }
    {
        std::lock_guard lock(pendingMutex_);
        pending_.clear();
        pendingUncertain_ = false;
        callback_ = {};
        excludePath_ = {};
    }
}

void DirectoryChangeMonitor::WatchMain(Watch& watch) {
    alignas(DWORD) std::array<std::byte, kNotificationBufferBytes> buffer{};
    bool recoveryRequired = watch.CurrentDirectory() == INVALID_HANDLE_VALUE;
    bool failureReported = false;
    const auto waitForRetry = [this] {
        return WaitForSingleObject(stopEvent_, kWatchRetryMilliseconds) == WAIT_TIMEOUT;
    };

    while (!stopping_.load(std::memory_order_acquire)) {
        HANDLE directory = watch.CurrentDirectory();
        if (directory == INVALID_HANDLE_VALUE) {
            directory = OpenDirectoryWatch(watch.root);
            if (directory == INVALID_HANDLE_VALUE) {
                recoveryRequired = true;
                if (!failureReported) {
                    Queue({}, true);
                    failureReported = true;
                }
                if (!waitForRetry()) {
                    return;
                }
                continue;
            }
            if (stopping_.load(std::memory_order_acquire)) {
                CloseHandle(directory);
                return;
            }
            watch.ReplaceDirectory(directory);
            if (recoveryRequired) {
                Queue({}, true);
            }
            recoveryRequired = false;
            failureReported = false;
        }

        HANDLE completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (completed == nullptr) {
            if (!failureReported) {
                Queue({}, true);
                failureReported = true;
            }
            recoveryRequired = true;
            watch.ReplaceDirectory(INVALID_HANDLE_VALUE);
            if (!waitForRetry()) {
                return;
            }
            continue;
        }

        OVERLAPPED operation{};
        operation.hEvent = completed;
        const BOOL started = ReadDirectoryChangesW(
            directory,
            buffer.data(),
            static_cast<DWORD>(buffer.size()),
            TRUE,
            FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME,
            nullptr,
            &operation,
            nullptr);
        if (!started) {
            const DWORD error = GetLastError();
            CloseHandle(completed);
            if (stopping_.load(std::memory_order_acquire) || error == ERROR_OPERATION_ABORTED) {
                return;
            }
            watch.ReplaceDirectory(INVALID_HANDLE_VALUE);
            recoveryRequired = true;
            if (!failureReported) {
                Queue({}, true);
                failureReported = true;
            }
            if (!waitForRetry()) {
                return;
            }
            continue;
        }

        const HANDLE waits[]{completed, stopEvent_};
        const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (wait != WAIT_OBJECT_0) {
            CancelIoEx(directory, &operation);
            DWORD ignored = 0;
            GetOverlappedResult(directory, &operation, &ignored, TRUE);
            CloseHandle(completed);
            return;
        }

        DWORD transferred = 0;
        const BOOL succeeded = GetOverlappedResult(
            directory, &operation, &transferred, FALSE);
        const DWORD completionError = succeeded ? ERROR_SUCCESS : GetLastError();
        CloseHandle(completed);
        if (!succeeded) {
            if (stopping_.load(std::memory_order_acquire) ||
                completionError == ERROR_OPERATION_ABORTED) {
                return;
            }
            watch.ReplaceDirectory(INVALID_HANDLE_VALUE);
            recoveryRequired = true;
            if (!failureReported) {
                Queue({}, true);
                failureReported = true;
            }
            if (!waitForRetry()) {
                return;
            }
            continue;
        }
        if (transferred == 0) {
            Queue({}, true);
            continue;
        }

        failureReported = false;

        std::vector<FileSystemChange> changes;
        std::size_t cursor = 0;
        bool malformed = false;
        while (cursor < transferred) {
            constexpr std::size_t headerSize = offsetof(FILE_NOTIFY_INFORMATION, FileName);
            const auto remaining = static_cast<std::size_t>(transferred) - cursor;
            if (remaining < headerSize) {
                malformed = true;
                break;
            }
            const auto* notification = reinterpret_cast<const FILE_NOTIFY_INFORMATION*>(
                buffer.data() + cursor);
            if (notification->FileNameLength == 0 ||
                (notification->FileNameLength % sizeof(wchar_t)) != 0 ||
                notification->FileNameLength > remaining - headerSize) {
                malformed = true;
                break;
            }

            const std::wstring_view relative(
                notification->FileName,
                notification->FileNameLength / sizeof(wchar_t));
            if (!relative.empty()) {
                FileSystemChangeAction action{};
                switch (notification->Action) {
                case FILE_ACTION_ADDED:
                    action = FileSystemChangeAction::UpsertAndReconcile;
                    break;
                case FILE_ACTION_MODIFIED:
                    action = FileSystemChangeAction::Upsert;
                    break;
                case FILE_ACTION_RENAMED_NEW_NAME:
                    action = FileSystemChangeAction::UpsertAndReconcile;
                    break;
                case FILE_ACTION_REMOVED:
                case FILE_ACTION_RENAMED_OLD_NAME:
                    action = FileSystemChangeAction::Remove;
                    break;
                default:
                    malformed = true;
                    break;
                }
                if (malformed) break;
                auto changedPath = (watch.root / relative).lexically_normal();
                if (!excludePath_ || !excludePath_(changedPath)) {
                    changes.push_back(FileSystemChange{std::move(changedPath), action});
                }
            }

            if (notification->NextEntryOffset == 0) {
                break;
            }
            const auto next = static_cast<std::size_t>(notification->NextEntryOffset);
            const auto occupied = headerSize + notification->FileNameLength;
            if ((next % alignof(DWORD)) != 0 || next < occupied || next >= remaining ||
                remaining - next < headerSize) {
                malformed = true;
                break;
            }
            cursor += next;
        }
        Queue(std::move(changes), malformed);
    }
}

void DirectoryChangeMonitor::PublisherMain() {
    std::unique_lock lock(pendingMutex_);
    while (!stopping_.load(std::memory_order_acquire)) {
        pendingChanged_.wait(lock, [this] {
            return stopping_.load(std::memory_order_acquire) ||
                pendingUncertain_ || !pending_.empty();
        });
        if (stopping_.load(std::memory_order_acquire)) {
            break;
        }

        const auto deadline = std::chrono::steady_clock::now() + kPublishDebounce;
        pendingChanged_.wait_until(lock, deadline, [this] {
            return stopping_.load(std::memory_order_acquire);
        });
        if (stopping_.load(std::memory_order_acquire)) {
            break;
        }

        auto changes = std::move(pending_);
        pending_.clear();
        const bool uncertain = std::exchange(pendingUncertain_, false);
        auto callback = callback_;
        lock.unlock();
        if (callback) {
            try {
                callback(std::move(changes), uncertain);
            } catch (...) {
                // An observer cannot terminate the monitor thread.
            }
        }
        lock.lock();
    }
}

void DirectoryChangeMonitor::Queue(
    std::vector<FileSystemChange> changes,
    const bool uncertain) {
    {
        std::lock_guard lock(pendingMutex_);
        if (stopping_.load(std::memory_order_acquire)) {
            return;
        }
        pendingUncertain_ |= uncertain;
        if (changes.size() > kMaximumPendingChanges - (std::min)(pending_.size(), kMaximumPendingChanges)) {
            pending_.clear();
            pendingUncertain_ = true;
        } else {
            pending_.insert(
                pending_.end(),
                std::make_move_iterator(changes.begin()),
                std::make_move_iterator(changes.end()));
        }
    }
    pendingChanged_.notify_one();
}

} // namespace luvletter::indexer
