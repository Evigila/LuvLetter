#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"

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

constexpr auto kRefreshInterval = std::chrono::hours(6);
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
        snapshot_ = IndexSnapshot::Load(snapshotPath_);
        const bool loadedSnapshot = snapshot_ != nullptr;
        if (!snapshot_) {
            snapshot_ = std::make_shared<const IndexSnapshot>();
        }
        status_.store(loadedSnapshot ? 2ULL : 0ULL, std::memory_order_relaxed);
        worker_ = std::thread([this] { WorkerMain(); });
    }

    ~IndexStore() {
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

    void Configure(std::vector<std::filesystem::path> roots) {
        {
            std::lock_guard lock(configurationMutex_);
            status_.fetch_or(1ULL, std::memory_order_release);
            roots_ = std::move(roots);
            hasConfiguration_ = true;
            ++configurationGeneration_;
            cancelBuild_.store(true, std::memory_order_relaxed);
        }
        configurationChanged_.notify_all();
    }

    [[nodiscard]] std::vector<luvletter::indexing::SearchResult> Query(
        const std::wstring_view query,
        const std::size_t maximumResults) const {
        std::shared_ptr<const IndexSnapshot> snapshot;
        {
            std::shared_lock lock(snapshotMutex_);
            snapshot = snapshot_;
        }
        return snapshot->Query(query, maximumResults);
    }

    [[nodiscard]] protocol::IndexStatus Status() const noexcept {
        const std::uint64_t state = status_.load(std::memory_order_acquire);
        return protocol::IndexStatus{state >> 1U, (state & 1U) != 0};
    }

private:
    void WorkerMain() {
        const bool backgroundMode = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) != FALSE;
        if (!backgroundMode) {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        }
        std::unique_lock lock(configurationMutex_);
        std::uint64_t completedGeneration = 0;
        while (!stopping_) {
            configurationChanged_.wait(lock, [this, completedGeneration] {
                return stopping_ || (hasConfiguration_ && configurationGeneration_ != completedGeneration);
            });
            if (stopping_) {
                break;
            }

            const auto roots = roots_;
            const auto generation = configurationGeneration_;
            cancelBuild_.store(false, std::memory_order_relaxed);
            lock.unlock();

            auto rebuilt = IndexBuilder::Build(roots, &cancelBuild_);

            lock.lock();
            if (stopping_) {
                break;
            }
            if (rebuilt && generation == configurationGeneration_) {
                {
                    std::unique_lock snapshotLock(snapshotMutex_);
                    snapshot_ = rebuilt;
                }
                const std::uint64_t currentStatus = status_.load(std::memory_order_relaxed);
                const std::uint64_t nextGeneration = (currentStatus >> 1U) + 1U;
                status_.store(nextGeneration << 1U, std::memory_order_release);
                completedGeneration = generation;

                lock.unlock();
                (void)rebuilt->Save(snapshotPath_);
                lock.lock();
                if (stopping_) {
                    break;
                }
                if (configurationGeneration_ != generation) {
                    continue;
                }

                const bool reconfigured = configurationChanged_.wait_for(
                    lock,
                    kRefreshInterval,
                    [this, generation] { return stopping_ || configurationGeneration_ != generation; });
                if (!reconfigured && !stopping_) {
                    status_.fetch_or(1ULL, std::memory_order_release);
                    ++configurationGeneration_;
                }
            }
        }
        if (backgroundMode) {
            SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
        }
    }

    const std::filesystem::path snapshotPath_;
    mutable std::shared_mutex snapshotMutex_;
    std::shared_ptr<const IndexSnapshot> snapshot_;

    std::mutex configurationMutex_;
    std::condition_variable configurationChanged_;
    std::vector<std::filesystem::path> roots_;
    std::uint64_t configurationGeneration_ = 0;
    std::atomic_uint64_t status_ = 0;
    std::atomic_bool cancelBuild_ = false;
    bool hasConfiguration_ = false;
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
    if (cursor != payload.size()) {
        return false;
    }
    store.Configure(std::move(roots));
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

    IndexStore store(options.dataDirectory / L"file-index-v2.bin");
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
        case protocol::MessageType::Status:
            if (!payload.empty()) {
                if (!SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Status payload must be empty.")) {
                    return 8;
                }
                break;
            }
            {
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
