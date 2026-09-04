#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"
#include "IndexMaintenance.h"
#include "IndexRebuildPolicy.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <shellapi.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <memory>
#include <mutex>
#include <optional>
#include <shared_mutex>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace {

using luvletter::indexing::IndexBuilder;
using luvletter::indexing::IndexSnapshot;
using luvletter::indexing::Utf8ToWide;
using luvletter::indexing::WideToUtf8;
namespace protocol = luvletter::indexing::protocol;

constexpr auto kMinimumRebuildInterval = std::chrono::minutes(1);
constexpr std::uint32_t kMaximumQueryResults = 256;

class UniqueHandle final {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(const HANDLE value) noexcept : value_(value) {}
    ~UniqueHandle() { Reset(); }

    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    UniqueHandle(UniqueHandle&& other) noexcept : value_(other.Release()) {}
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) {
            Reset(other.Release());
        }
        return *this;
    }

    [[nodiscard]] HANDLE Get() const noexcept { return value_; }
    [[nodiscard]] explicit operator bool() const noexcept {
        return value_ != nullptr && value_ != INVALID_HANDLE_VALUE;
    }
    [[nodiscard]] HANDLE Release() noexcept {
        const HANDLE result = value_;
        value_ = nullptr;
        return result;
    }
    void Reset(const HANDLE value = nullptr) noexcept {
        if (*this) {
            CloseHandle(value_);
        }
        value_ = value;
    }

private:
    HANDLE value_ = nullptr;
};

struct Options final {
    std::wstring pipeName;
    DWORD parentProcessId = 0;
    std::filesystem::path dataDirectory;
};

bool TryParseUnsigned(const std::wstring& text, DWORD& value) {
    if (text.empty()) {
        return false;
    }
    wchar_t* end = nullptr;
    const unsigned long parsed = wcstoul(text.c_str(), &end, 10);
    if (end == nullptr || *end != L'\0' || parsed == 0) {
        return false;
    }
    value = static_cast<DWORD>(parsed);
    return true;
}

std::optional<Options> ParseOptions(const int argumentCount, wchar_t** arguments) {
    Options options;
    for (int index = 1; index + 1 < argumentCount; index += 2) {
        const std::wstring_view name(arguments[index]);
        if (name == L"--pipe") {
            options.pipeName = arguments[index + 1];
        } else if (name == L"--parent-pid") {
            if (!TryParseUnsigned(arguments[index + 1], options.parentProcessId)) {
                return std::nullopt;
            }
        } else if (name == L"--data-dir") {
            options.dataDirectory = arguments[index + 1];
        } else {
            return std::nullopt;
        }
    }

    if (argumentCount != 7 || options.pipeName.empty() || options.parentProcessId == 0 || options.dataDirectory.empty()) {
        return std::nullopt;
    }
    return options;
}

std::wstring FullPipeName(const std::wstring_view name) {
    constexpr std::wstring_view prefix = L"\\\\.\\pipe\\";
    if (name.starts_with(prefix)) {
        return std::wstring(name);
    }
    return std::wstring(prefix) + std::wstring(name);
}

bool ParentIsAlive(const HANDLE parentProcess) {
    return WaitForSingleObject(parentProcess, 0) == WAIT_TIMEOUT;
}

UniqueHandle ConnectToPipe(const std::wstring& pipeName, const HANDLE parentProcess) {
    while (ParentIsAlive(parentProcess)) {
        const HANDLE pipe = CreateFileW(
            pipeName.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            nullptr);
        if (pipe != INVALID_HANDLE_VALUE) {
            DWORD mode = PIPE_READMODE_BYTE;
            SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr);
            return UniqueHandle(pipe);
        }

        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) {
            return {};
        }
        WaitNamedPipeW(pipeName.c_str(), 250);
    }
    return {};
}

bool Transfer(
    const HANDLE pipe,
    const HANDLE parentProcess,
    void* buffer,
    const std::size_t length,
    const bool write) {
    auto* bytes = static_cast<std::byte*>(buffer);
    std::size_t cursor = 0;
    while (cursor < length) {
        UniqueHandle completed(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!completed) {
            return false;
        }

        OVERLAPPED operation{};
        operation.hEvent = completed.Get();
        const DWORD requested = static_cast<DWORD>(
            (std::min<std::size_t>)(length - cursor, static_cast<std::size_t>(MAXDWORD)));
        DWORD transferred = 0;
        const BOOL started = write
            ? WriteFile(pipe, bytes + cursor, requested, &transferred, &operation)
            : ReadFile(pipe, bytes + cursor, requested, &transferred, &operation);
        if (!started) {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING) {
                return false;
            }

            const HANDLE waits[] = {completed.Get(), parentProcess};
            const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
            if (wait != WAIT_OBJECT_0) {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) {
                return false;
            }
        }
        if (transferred == 0) {
            return false;
        }
        cursor += transferred;
    }
    return true;
}

class IndexStore final {
public:
    explicit IndexStore(std::filesystem::path snapshotPath)
        : snapshotPath_(std::move(snapshotPath)) {
        snapshot_ = std::make_shared<const IndexSnapshot>();
        worker_ = std::thread([this] { WorkerMain(); });
    }

    ~IndexStore() {
        changeMonitor_.Stop();
        {
            std::lock_guard lock(configurationMutex_);
            stopping_ = true;
            cancelBuild_.store(true, std::memory_order_relaxed);
        }
        configurationChanged_.notify_all();
        if (worker_.joinable()) {
            worker_.join();
        }
    }

    IndexStore(const IndexStore&) = delete;
    IndexStore& operator=(const IndexStore&) = delete;

    void Configure(
        std::vector<std::filesystem::path> roots,
        const std::chrono::seconds refreshInterval,
        const std::chrono::seconds triggerCooldown,
        const std::vector<std::filesystem::path>& ignoredDirectories,
        const std::vector<std::wstring>& ignoredDirectoryNames) {
        changeMonitor_.Stop();
        const auto watcherRoots = roots;
        {
            std::lock_guard lock(configurationMutex_);
            std::unique_lock viewLock(viewMutex_);
            delta_.Clear();
            std::shared_ptr<const IndexSnapshot> compatibleSnapshot;
            {
                std::shared_lock snapshotLock(snapshotMutex_);
                if (snapshot_->MatchesRoots(roots)) {
                    compatibleSnapshot = snapshot_;
                }
            }
            if (!compatibleSnapshot && cachedSnapshot_ && cachedSnapshot_->MatchesRoots(roots)) {
                compatibleSnapshot = cachedSnapshot_;
            }
            const bool hasCompatibleSnapshot = compatibleSnapshot != nullptr;
            hasUsableSnapshot_ = hasCompatibleSnapshot;
            if (!compatibleSnapshot) {
                compatibleSnapshot = std::make_shared<const IndexSnapshot>();
            }
            {
                std::unique_lock snapshotLock(snapshotMutex_);
                snapshot_ = std::move(compatibleSnapshot);
            }

            const std::uint64_t currentStatus = status_.load(std::memory_order_relaxed);
            const std::uint64_t generation = hasCompatibleSnapshot
                ? (std::max)(1ULL, currentStatus >> kActivityBits)
                : currentStatus >> kActivityBits;
            const auto activity = hasCompatibleSnapshot
                ? protocol::IndexActivity::Updating
                : protocol::IndexActivity::InitialBuild;
            status_.store(PackStatus(generation, activity), std::memory_order_release);
            roots_ = std::move(roots);
            refreshInterval_ = refreshInterval;
            rebuildPolicy_.Configure(ignoredDirectories, triggerCooldown, ignoredDirectoryNames);
            hasConfiguration_ = true;
            ++configurationGeneration_;
            rebuildRequested_ = true;
            forcedRefreshRequested_ = false;
            nextMaintenanceAllowed_ = (std::chrono::steady_clock::time_point::min)();
            cancelBuild_.store(true, std::memory_order_relaxed);
        }
        try {
            changeMonitor_.Start(watcherRoots, [this](
                std::vector<luvletter::indexer::FileSystemChange> changes,
                const bool uncertain) {
                ApplyFileSystemChanges(std::move(changes), uncertain);
            });
        } catch (...) {
            // Full reconciliation remains available when change monitoring cannot start.
        }
        configurationChanged_.notify_all();
    }

    void RequestRefresh() {
        {
            std::lock_guard lock(configurationMutex_);
            if (stopping_ || !hasConfiguration_) return;
            rebuildRequested_ = true;
            forcedRefreshRequested_ = true;
        }
        std::cerr << "[Index] Manual refresh queued (cooldown bypassed).\n";
        configurationChanged_.notify_all();
    }

    [[nodiscard]] std::vector<luvletter::indexing::SearchResult> Query(
        const std::wstring_view query,
        const std::size_t maximumResults) const {
        std::shared_lock viewLock(viewMutex_);
        std::shared_ptr<const IndexSnapshot> snapshot;
        {
            std::shared_lock lock(snapshotMutex_);
            snapshot = snapshot_;
        }
        return delta_.Query(query, *snapshot, maximumResults);
    }

    [[nodiscard]] protocol::IndexStatus Status() const noexcept {
        const std::uint64_t state = status_.load(std::memory_order_acquire);
        return protocol::IndexStatus{
            state >> kActivityBits,
            static_cast<protocol::IndexActivity>(state & kActivityMask)};
    }

private:
    static constexpr unsigned kActivityBits = 2;
    static constexpr std::uint64_t kActivityMask = (1ULL << kActivityBits) - 1ULL;

    [[nodiscard]] static constexpr std::uint64_t PackStatus(
        const std::uint64_t generation,
        const protocol::IndexActivity activity) noexcept {
        return (generation << kActivityBits) | static_cast<std::uint64_t>(activity);
    }

    void SetActivity(const protocol::IndexActivity activity) noexcept {
        std::uint64_t current = status_.load(std::memory_order_acquire);
        while (!status_.compare_exchange_weak(
            current,
            (current & ~kActivityMask) | static_cast<std::uint64_t>(activity),
            std::memory_order_release,
            std::memory_order_acquire)) {
        }
    }

    void ApplyFileSystemChanges(
        std::vector<luvletter::indexer::FileSystemChange> changes,
        const bool uncertain) {
        // Snapshot writes must not feed changes back into their own index.
        const auto cacheDirectory = snapshotPath_.parent_path();
        std::erase_if(changes, [&cacheDirectory](const auto& change) {
            return CompareStringOrdinal(
                change.path.parent_path().c_str(), -1,
                cacheDirectory.c_str(), -1, TRUE) == CSTR_EQUAL;
        });
        bool deltaLimitReached = false;
        std::vector<std::filesystem::path> rebuildCauses;
        {
            std::shared_lock viewLock(viewMutex_);
            deltaLimitReached = delta_.Apply(changes, &rebuildCauses);
        }
        if (!changes.empty()) {
            status_.fetch_add(1ULL << kActivityBits, std::memory_order_acq_rel);
        }
        if (!uncertain && !deltaLimitReached) {
            return;
        }

        const auto now = std::chrono::steady_clock::now();
        bool accepted = uncertain && rebuildPolicy_.AcceptUnknown(now);
        std::filesystem::path acceptedCause;
        for (const auto& cause : rebuildCauses) {
            if (rebuildPolicy_.Accept(cause, now)) {
                accepted = true;
                if (acceptedCause.empty()) acceptedCause = cause;
            }
        }
        if (!accepted) return;

        {
            std::lock_guard lock(configurationMutex_);
            if (stopping_ || !hasConfiguration_) {
                return;
            }
            // Coalesce watcher recovery and directory churn without cancelling a scan.
            // Only a real root reconfiguration invalidates the in-flight build.
            if (!rebuildRequested_) {
                std::cerr << "[Index] Automatic rebuild queued: "
                    << (acceptedCause.empty() ? "watcher recovery" : WideToUtf8(acceptedCause.native()))
                    << "\n";
            }
            rebuildRequested_ = true;
        }
        configurationChanged_.notify_all();
    }

    void WorkerMain() {
        const bool backgroundMode = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) != FALSE;
        if (!backgroundMode) {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        }
        std::unique_lock lock(configurationMutex_);
        bool cacheLoadAttempted = false;
        while (!stopping_) {
            configurationChanged_.wait(lock, [this] {
                return stopping_ || hasConfiguration_;
            });
            if (stopping_) {
                break;
            }

            const auto roots = roots_;
            const auto generation = configurationGeneration_;
            if (!cacheLoadAttempted) {
                cacheLoadAttempted = true;
                lock.unlock();
                // Keep cache I/O and validation off the pipe/handshake thread.
                const auto loadCache = [](const std::filesystem::path& path) {
                    try {
                        return IndexSnapshot::Load(path);
                    } catch (...) {
                        std::cerr << "[Index] Could not load the index snapshot.\n";
                        return std::shared_ptr<const IndexSnapshot>{};
                    }
                };
                auto cached = loadCache(snapshotPath_);
                if (!cached || !cached->MatchesRoots(roots)) {
                    auto backupPath = snapshotPath_;
                    backupPath += L".bak";
                    cached = loadCache(backupPath);
                }
                lock.lock();
                cachedSnapshot_ = std::move(cached);
                if (stopping_) break;
                if (cachedSnapshot_ && cachedSnapshot_->MatchesRoots(roots_)) {
                    std::unique_lock viewLock(viewMutex_);
                    std::unique_lock snapshotLock(snapshotMutex_);
                    snapshot_ = cachedSnapshot_;
                    hasUsableSnapshot_ = true;
                    status_.fetch_add(1ULL << kActivityBits, std::memory_order_acq_rel);
                    SetActivity(protocol::IndexActivity::Ready);
                    std::cerr << "[Index] Cached snapshot is available for queries.\n";
                }
                continue;
            }

            const bool requested = rebuildRequested_;
            const bool forced = forcedRefreshRequested_;
            const auto deadline = forced ? (std::chrono::steady_clock::time_point::min)()
                : requested ? nextMaintenanceAllowed_ : nextRefresh_;
            if (std::chrono::steady_clock::now() < deadline) {
                configurationChanged_.wait_until(lock, deadline, [this, generation, requested, forced] {
                    return stopping_ || configurationGeneration_ != generation ||
                        rebuildRequested_ != requested || forcedRefreshRequested_ != forced;
                });
                continue;
            }
            rebuildRequested_ = false;
            forcedRefreshRequested_ = false;
            const auto deltaRevision = delta_.CaptureRevision();
            cancelBuild_.store(false, std::memory_order_relaxed);
            SetActivity(hasUsableSnapshot_
                ? protocol::IndexActivity::Updating
                : protocol::IndexActivity::InitialBuild);
            lock.unlock();

            const auto scanStarted = std::chrono::steady_clock::now();
            std::cerr << "[Index] Full scan started" << (forced ? " (manual).\n" : ".\n");
            std::shared_ptr<const IndexSnapshot> rebuilt;
            try {
                rebuilt = IndexBuilder::Build(roots, &cancelBuild_);
            } catch (...) {
                std::cerr << "[Index] Rebuild failed; retaining the previous snapshot.\n";
            }

            lock.lock();
            if (stopping_) {
                break;
            }
            if (generation != configurationGeneration_) continue;
            nextMaintenanceAllowed_ = std::chrono::steady_clock::now() + kMinimumRebuildInterval;
            if (rebuilt) {
                hasUsableSnapshot_ = true;
                {
                    std::unique_lock viewLock(viewMutex_);
                    {
                        std::unique_lock snapshotLock(snapshotMutex_);
                        snapshot_ = rebuilt;
                    }
                    delta_.PruneThrough(deltaRevision);
                }
                std::uint64_t currentStatus = status_.load(std::memory_order_acquire);
                while (!status_.compare_exchange_weak(
                    currentStatus,
                    PackStatus(
                        (currentStatus >> kActivityBits) + 1U,
                        protocol::IndexActivity::Ready),
                    std::memory_order_release,
                    std::memory_order_acquire)) {
                }
                const auto previousCache = cachedSnapshot_;
                lock.unlock();
                bool saved = false;
                try {
                    auto backupPath = snapshotPath_;
                    backupPath += L".bak";
                    // Keep the preceding valid snapshot if the primary is later damaged.
                    const auto backup = previousCache && previousCache->MatchesRoots(roots)
                        ? previousCache : rebuilt;
                    if (backup->Save(backupPath)) {
                        saved = rebuilt->Save(snapshotPath_);
                    }
                } catch (...) {
                    // Failed persistence must not discard the last usable cache.
                }
                if (!saved) std::cerr << "[Index] Cache save failed; previous cache retained.\n";
                std::cerr << "[Index] Full scan completed in "
                    << std::chrono::duration_cast<std::chrono::seconds>(
                        std::chrono::steady_clock::now() - scanStarted).count()
                    << " seconds; cache " << (saved ? "saved.\n" : "not saved.\n");
                lock.lock();
                if (saved) cachedSnapshot_ = rebuilt;
                nextRefresh_ = std::chrono::steady_clock::now() + refreshInterval_;
            } else {
                // Avoid a tight retry loop; queries keep using the previous snapshot.
                rebuildRequested_ = true;
                SetActivity(protocol::IndexActivity::Failed);
                std::cerr << "[Index] Rebuild failed; retry deferred for one minute.\n";
            }
        }
        if (backgroundMode) {
            SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
        }
    }

    const std::filesystem::path snapshotPath_;
    mutable std::shared_mutex viewMutex_;
    mutable std::shared_mutex snapshotMutex_;
    std::shared_ptr<const IndexSnapshot> snapshot_;
    std::shared_ptr<const IndexSnapshot> cachedSnapshot_;
    luvletter::indexer::LiveIndexDelta delta_;
    luvletter::indexer::IndexRebuildPolicy rebuildPolicy_;
    luvletter::indexer::DirectoryChangeMonitor changeMonitor_;

    std::mutex configurationMutex_;
    std::condition_variable configurationChanged_;
    std::vector<std::filesystem::path> roots_;
    std::uint64_t configurationGeneration_ = 0;
    std::atomic_uint64_t status_ = 0;
    std::atomic_bool cancelBuild_ = false;
    bool hasConfiguration_ = false;
    bool hasUsableSnapshot_ = false;
    bool rebuildRequested_ = false;
    bool forcedRefreshRequested_ = false;
    std::chrono::seconds refreshInterval_{360};
    std::chrono::steady_clock::time_point nextMaintenanceAllowed_{};
    std::chrono::steady_clock::time_point nextRefresh_{};
    bool stopping_ = false;
    std::thread worker_;
};

bool SendFrame(
    const HANDLE pipe,
    const HANDLE parentProcess,
    const protocol::MessageType type,
    const std::uint64_t requestId,
    const std::span<const std::byte> payload = {}) {
    if (payload.size() > protocol::kMaximumPayloadSize) {
        return false;
    }
    const protocol::FrameHeader header{
        protocol::kMagic,
        protocol::kMajorVersion,
        type,
        static_cast<std::uint32_t>(payload.size()),
        requestId};
    auto headerBytes = protocol::EncodeHeader(header);
    return Transfer(pipe, parentProcess, headerBytes.data(), headerBytes.size(), true) &&
        (payload.empty() || Transfer(
            pipe,
            parentProcess,
            const_cast<std::byte*>(payload.data()),
            payload.size(),
            true));
}

bool SendError(
    const HANDLE pipe,
    const HANDLE parentProcess,
    const std::uint64_t requestId,
    const std::string_view message) {
    std::vector<std::byte> payload;
    protocol::AppendUtf8(payload, message);
    return SendFrame(pipe, parentProcess, protocol::MessageType::Error, requestId, payload);
}

bool ConfigureRoots(IndexStore& store, const std::span<const std::byte> payload) {
    std::size_t cursor = 0;
    std::uint32_t count = 0;
    if (!protocol::ReadU32(payload, cursor, count) || count > 1024) {
        return false;
    }

    std::vector<std::filesystem::path> roots;
    roots.reserve(count);
    for (std::uint32_t index = 0; index < count; ++index) {
        std::string encoded;
        if (!protocol::ReadUtf8(payload, cursor, encoded)) {
            return false;
        }
        auto decoded = Utf8ToWide(encoded);
        if (decoded.empty() && !encoded.empty()) {
            return false;
        }
        roots.emplace_back(std::move(decoded));
    }
    std::uint32_t refreshSeconds = 0;
    std::uint32_t cooldownSeconds = 0;
    std::uint32_t ignoredCount = 0;
    if (!protocol::ReadU32(payload, cursor, refreshSeconds) || refreshSeconds < 60 || refreshSeconds > 86400 ||
        !protocol::ReadU32(payload, cursor, cooldownSeconds) || cooldownSeconds < 1 || cooldownSeconds > 3600 ||
        !protocol::ReadU32(payload, cursor, ignoredCount) || ignoredCount > 1024) {
        return false;
    }
    std::vector<std::filesystem::path> ignoredDirectories;
    ignoredDirectories.reserve(ignoredCount);
    for (std::uint32_t index = 0; index < ignoredCount; ++index) {
        std::string encoded;
        if (!protocol::ReadUtf8(payload, cursor, encoded)) return false;
        auto decoded = Utf8ToWide(encoded);
        const std::filesystem::path directory(decoded);
        if (decoded.empty() || !directory.is_absolute()) return false;
        ignoredDirectories.push_back(directory);
    }
    std::uint32_t ignoredNameCount = 0;
    if (!protocol::ReadU32(payload, cursor, ignoredNameCount) || ignoredNameCount > 128) {
        return false;
    }
    std::vector<std::wstring> ignoredDirectoryNames;
    ignoredDirectoryNames.reserve(ignoredNameCount);
    for (std::uint32_t index = 0; index < ignoredNameCount; ++index) {
        std::string encoded;
        if (!protocol::ReadUtf8(payload, cursor, encoded)) return false;
        auto decoded = Utf8ToWide(encoded);
        if (!luvletter::indexer::IndexRebuildPolicy::IsValidIgnoredDirectoryName(decoded)) return false;
        ignoredDirectoryNames.push_back(std::move(decoded));
    }
    if (cursor != payload.size()) {
        return false;
    }
    store.Configure(std::move(roots), std::chrono::seconds(refreshSeconds),
        std::chrono::seconds(cooldownSeconds), ignoredDirectories, ignoredDirectoryNames);
    return true;
}

bool HandleQuery(
    IndexStore& store,
    const HANDLE pipe,
    const HANDLE parentProcess,
    const std::uint64_t requestId,
    const std::span<const std::byte> payload) {
    std::size_t cursor = 0;
    std::uint64_t editorRevision = 0;
    std::uint32_t requestedMaximum = 0;
    std::string encodedQuery;
    if (!protocol::ReadU64(payload, cursor, editorRevision) ||
        !protocol::ReadU32(payload, cursor, requestedMaximum) ||
        !protocol::ReadUtf8(payload, cursor, encodedQuery) ||
        cursor != payload.size()) {
        return SendError(pipe, parentProcess, requestId, "Malformed Query payload.");
    }

    const auto query = Utf8ToWide(encodedQuery);
    if (query.empty() && !encodedQuery.empty()) {
        return SendError(pipe, parentProcess, requestId, "Query is not valid UTF-8.");
    }
    const std::size_t maximum = (std::min)(requestedMaximum, kMaximumQueryResults);
    const auto results = store.Query(query, maximum);

    std::vector<std::byte> encodedItems;
    std::uint32_t encodedCount = 0;
    for (const auto& result : results) {
        std::vector<std::byte> item;
        protocol::AppendU64(item, result.stableId);
        protocol::AppendU32(item, static_cast<std::uint32_t>(result.kind));
        protocol::AppendUtf8(item, WideToUtf8(result.displayName));
        protocol::AppendUtf8(item, WideToUtf8(result.fullPath));
        if (sizeof(editorRevision) + sizeof(encodedCount) + encodedItems.size() + item.size() >
            protocol::kMaximumPayloadSize) {
            break;
        }
        encodedItems.insert(encodedItems.end(), item.begin(), item.end());
        ++encodedCount;
    }

    std::vector<std::byte> response;
    response.reserve(sizeof(editorRevision) + sizeof(encodedCount) + encodedItems.size());
    protocol::AppendU64(response, editorRevision);
    protocol::AppendU32(response, encodedCount);
    response.insert(response.end(), encodedItems.begin(), encodedItems.end());
    return SendFrame(pipe, parentProcess, protocol::MessageType::QueryResult, requestId, response);
}

int Run(const Options& options) {
    UniqueHandle parentProcess(OpenProcess(SYNCHRONIZE, FALSE, options.parentProcessId));
    if (!parentProcess) {
        return 2;
    }

    auto pipe = ConnectToPipe(FullPipeName(options.pipeName), parentProcess.Get());
    if (!pipe) {
        return 3;
    }

    IndexStore store(options.dataDirectory / L"file-index-v3.bin");
    bool handshakeComplete = false;
    while (ParentIsAlive(parentProcess.Get())) {
        std::vector<std::byte> headerBytes(protocol::kHeaderSize);
        if (!Transfer(pipe.Get(), parentProcess.Get(), headerBytes.data(), headerBytes.size(), false)) {
            break;
        }

        protocol::FrameHeader header{};
        if (!protocol::DecodeHeader(headerBytes, header) ||
            header.magic != protocol::kMagic ||
            header.majorVersion != protocol::kMajorVersion ||
            header.payloadLength > protocol::kMaximumPayloadSize) {
            SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Invalid frame header.");
            break;
        }

        std::vector<std::byte> payload(header.payloadLength);
        if (!payload.empty() && !Transfer(pipe.Get(), parentProcess.Get(), payload.data(), payload.size(), false)) {
            break;
        }

        if (!handshakeComplete) {
            if (header.type != protocol::MessageType::Hello || !payload.empty()) {
                SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Hello must be the first frame.");
                break;
            }
            if (!SendFrame(
                    pipe.Get(), parentProcess.Get(), protocol::MessageType::HelloAck, header.requestId)) {
                break;
            }
            handshakeComplete = true;
            continue;
        }

        switch (header.type) {
        case protocol::MessageType::ConfigureRoots:
            if (!ConfigureRoots(store, payload) &&
                !SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Malformed ConfigureRoots payload.")) {
                return 4;
            }
            break;
        case protocol::MessageType::Query:
            if (!HandleQuery(store, pipe.Get(), parentProcess.Get(), header.requestId, payload)) {
                return 5;
            }
            break;
        case protocol::MessageType::Refresh:
        case protocol::MessageType::Status:
            if (!payload.empty()) {
                if (!SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Status/Refresh payload must be empty.")) {
                    return 8;
                }
                break;
            }
            {
                if (header.type == protocol::MessageType::Refresh) store.RequestRefresh();
                const auto statusPayload = protocol::EncodeStatus(store.Status());
                if (!SendFrame(
                        pipe.Get(),
                        parentProcess.Get(),
                        protocol::MessageType::Status,
                        header.requestId,
                        statusPayload)) {
                    return 9;
                }
            }
            break;
        case protocol::MessageType::Shutdown:
            return payload.empty() ? 0 : 6;
        default:
            if (!SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Unsupported message type.")) {
                return 7;
            }
            break;
        }
    }
    return 0;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argumentCount = 0;
    wchar_t** arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments == nullptr) {
        return 1;
    }
    const auto options = ParseOptions(argumentCount, arguments);
    LocalFree(arguments);
    return options.has_value() ? Run(*options) : 1;
}
