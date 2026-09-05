#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"
#include "IndexDiagnostics.h"
#include "IndexMaintenance.h"
#include "IndexPartitioning.h"
#include "IndexRebuildPolicy.h"
#include "PartitionedIndexStore.h"

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
#include <syncstream>
#include <thread>
#include <utility>
#include <vector>

namespace {

using luvletter::indexing::Utf8ToWide;
using luvletter::indexing::WideToUtf8;
namespace protocol = luvletter::indexing::protocol;

constexpr std::uint32_t kMaximumQueryResults = 256;

void LogIndex(const std::string_view event, const std::string_view message) {
    std::osyncstream(std::cout) << "[Index][" << event << "] " << message << std::endl;
}

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
    std::filesystem::path diagnosticLogPath;
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
        } else if (name == L"--diagnostic-log") {
            options.diagnosticLogPath = arguments[index + 1];
        } else {
            return std::nullopt;
        }
    }

    if (argumentCount < 7 || (argumentCount % 2) == 0 || options.pipeName.empty() ||
        options.parentProcessId == 0 || options.dataDirectory.empty()) {
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

bool ConfigureRoots(luvletter::indexer::PartitionedIndexStore& store, const std::span<const std::byte> payload) {
    std::size_t cursor = 0;
    std::uint32_t count = 0;
    if (!protocol::ReadU32(payload, cursor, count) || count > 1024) {
        return false;
    }

    std::vector<luvletter::indexer::IndexPartitionDescriptor> partitions;
    partitions.reserve(count);
    for (std::uint32_t index = 0; index < count; ++index) {
        std::string id;
        std::string encodedRoot;
        std::uint32_t tier = 0;
        std::uint32_t partitionRefresh = 0;
        std::uint32_t automaticGap = 0;
        std::uint32_t delegatedCount = 0;
        if (!protocol::ReadUtf8(payload, cursor, id) || !luvletter::indexer::IsValidPartitionId(id)
            || !protocol::ReadUtf8(payload, cursor, encodedRoot)
            || !protocol::ReadU32(payload, cursor, tier) || tier > 1
            || !protocol::ReadU32(payload, cursor, partitionRefresh)
            || partitionRefresh < 60 || partitionRefresh > 86400
            || !protocol::ReadU32(payload, cursor, automaticGap)
            || automaticGap < 1 || automaticGap > 3600
            || !protocol::ReadU32(payload, cursor, delegatedCount) || delegatedCount > 1024) return false;
        const std::filesystem::path root(Utf8ToWide(encodedRoot));
        if (root.empty() || !root.is_absolute()) return false;
        std::vector<std::filesystem::path> delegatedSubtrees;
        delegatedSubtrees.reserve(delegatedCount);
        for (std::uint32_t delegated = 0; delegated < delegatedCount; ++delegated) {
            std::string encoded;
            if (!protocol::ReadUtf8(payload, cursor, encoded)) return false;
            const std::filesystem::path path(Utf8ToWide(encoded));
            if (path.empty() || !path.is_absolute()
                || !luvletter::indexer::PartitionContainsPath(root, path)) return false;
            delegatedSubtrees.push_back(path);
        }
        partitions.push_back(luvletter::indexer::IndexPartitionDescriptor{
            std::move(id), root, std::move(delegatedSubtrees),
            static_cast<luvletter::indexer::PartitionMaintenanceTier>(tier),
            std::chrono::seconds(partitionRefresh), std::chrono::seconds(automaticGap)});
    }
    std::uint32_t cooldownSeconds = 0;
    std::uint32_t ignoredCount = 0;
    if (count == 0 || !protocol::ReadU32(payload, cursor, cooldownSeconds) || cooldownSeconds < 1 || cooldownSeconds > 3600 ||
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
    std::uint32_t fullIgnoreCount = 0;
    if (!protocol::ReadU32(payload, cursor, fullIgnoreCount) || fullIgnoreCount > 1024) return false;
    std::vector<std::filesystem::path> fullIgnorePaths;
    fullIgnorePaths.reserve(fullIgnoreCount);
    for (std::uint32_t index = 0; index < fullIgnoreCount; ++index) {
        std::string encoded;
        if (!protocol::ReadUtf8(payload, cursor, encoded)) return false;
        const std::filesystem::path path(Utf8ToWide(encoded));
        if (path.empty() || !path.is_absolute()) return false;
        fullIgnorePaths.push_back(path);
    }
    if (cursor != payload.size()) {
        return false;
    }
    return store.Configure(std::move(partitions), std::chrono::seconds(cooldownSeconds),
        ignoredDirectories, ignoredDirectoryNames, fullIgnorePaths);
}

bool HandleQuery(
    luvletter::indexer::PartitionedIndexStore& store,
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
    auto diagnosticLog = luvletter::indexer::DiagnosticLog::Open(options.diagnosticLogPath);
    if (diagnosticLog) {
        diagnosticLog->Write(L"process_started", L"Indexer diagnostic logging is enabled.");
    }
    UniqueHandle parentProcess(OpenProcess(SYNCHRONIZE, FALSE, options.parentProcessId));
    if (!parentProcess) {
        if (diagnosticLog) diagnosticLog->Write(L"parent_open_failed");
        return 2;
    }

    auto pipe = ConnectToPipe(FullPipeName(options.pipeName), parentProcess.Get());
    if (!pipe) {
        if (diagnosticLog) diagnosticLog->Write(L"pipe_connect_failed");
        return 3;
    }

    if (diagnosticLog) diagnosticLog->Write(L"pipe_connected");
    luvletter::indexer::PartitionedIndexStore store(options.dataDirectory,
        [](const std::string_view event, const std::string_view message) { LogIndex(event, message); },
        diagnosticLog.get(), options.diagnosticLogPath);
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
        case protocol::MessageType::Reconcile:
        case protocol::MessageType::Status:
            if (!payload.empty()) {
                if (!SendError(pipe.Get(), parentProcess.Get(), header.requestId, "Status/Refresh payload must be empty.")) {
                    return 8;
                }
                break;
            }
            {
                if (header.type == protocol::MessageType::Refresh) store.RequestRefresh(true);
                else if (header.type == protocol::MessageType::Reconcile) store.RequestRefresh(false);
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
    if (diagnosticLog) diagnosticLog->Write(L"process_stopped");
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
