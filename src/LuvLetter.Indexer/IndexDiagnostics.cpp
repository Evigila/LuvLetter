#include "IndexDiagnostics.h"

#include "luvletter/indexing/FileIndex.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <cstdio>
#include <string>
#include <utility>

namespace luvletter::indexer {
namespace {

constexpr std::uint64_t kMaximumLogBytes = 4ULL * 1024ULL * 1024ULL;
constexpr std::size_t kMaximumDetailCharacters = 3072;
constexpr std::size_t kMaximumEventCharacters = 256;

HANDLE OpenLogFile(const std::filesystem::path& path) noexcept {
    return CreateFileW(
        path.c_str(), FILE_APPEND_DATA | FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
}

std::string EscapeJson(const std::wstring_view value) {
    std::string escaped;
    const auto utf8 = indexing::WideToUtf8(value);
    escaped.reserve(utf8.size());
    constexpr char hex[] = "0123456789ABCDEF";
    for (const unsigned char character : utf8) {
        switch (character) {
        case '\\': escaped += "\\\\"; break;
        case '"': escaped += "\\\""; break;
        case '\r': escaped += "\\r"; break;
        case '\n': escaped += "\\n"; break;
        case '\t': escaped += "\\t"; break;
        default:
            if (character < 0x20U) {
                escaped += "\\u00";
                escaped.push_back(hex[character >> 4U]);
                escaped.push_back(hex[character & 0x0FU]);
            } else {
                escaped.push_back(static_cast<char>(character));
            }
            break;
        }
    }
    return escaped;
}

} // namespace

DiagnosticLog::DiagnosticLog(std::filesystem::path path) noexcept
    : path_(std::move(path)) {}

DiagnosticLog::~DiagnosticLog() {
    std::lock_guard lock(mutex_);
    if (fileHandle_ != nullptr) {
        FlushFileBuffers(static_cast<HANDLE>(fileHandle_));
        CloseHandle(static_cast<HANDLE>(fileHandle_));
    }
}

std::unique_ptr<DiagnosticLog> DiagnosticLog::Open(
    const std::filesystem::path& path) noexcept {
    try {
        if (path.empty()) return {};
        std::error_code error;
        if (!path.parent_path().empty()) {
            std::filesystem::create_directories(path.parent_path(), error);
        }
        if (error) return {};

        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) {
            const auto length = static_cast<std::uint64_t>(attributes.nFileSizeLow) |
                static_cast<std::uint64_t>(attributes.nFileSizeHigh) << 32U;
            if (length >= kMaximumLogBytes) {
                auto previous = path;
                previous += L".previous";
                if (!MoveFileExW(path.c_str(), previous.c_str(), MOVEFILE_REPLACE_EXISTING)) {
                    return {};
                }
            }
        }

        auto log = std::unique_ptr<DiagnosticLog>(new DiagnosticLog(path));
        const HANDLE file = OpenLogFile(path);
        if (file == INVALID_HANDLE_VALUE) return {};
        log->fileHandle_ = file;
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || length.QuadPart < 0) return {};
        log->writtenBytes_ = static_cast<std::uint64_t>(length.QuadPart);
        return log;
    } catch (...) {
        return {};
    }
}

void DiagnosticLog::Write(
    const std::wstring_view event,
    const std::wstring_view detail) noexcept {
    try {
        std::lock_guard lock(mutex_);
        if (fileHandle_ == nullptr) return;

        SYSTEMTIME time{};
        GetSystemTime(&time);
        char timestamp[32]{};
        sprintf_s(
            timestamp,
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        const auto boundedDetail = detail.substr(
            0, (std::min)(detail.size(), kMaximumDetailCharacters));
        const auto boundedEvent = event.substr(
            0, (std::min)(event.size(), kMaximumEventCharacters));
        auto line = std::string("{\"time\":\"") + timestamp +
            "\",\"event\":\"" + EscapeJson(boundedEvent) +
            "\",\"detail\":\"" + EscapeJson(boundedDetail) + "\"}\r\n";
        if (writtenBytes_ + line.size() > kMaximumLogBytes) {
            auto previous = path_;
            previous += L".previous";
            CloseHandle(static_cast<HANDLE>(fileHandle_));
            fileHandle_ = nullptr;
            if (!MoveFileExW(path_.c_str(), previous.c_str(), MOVEFILE_REPLACE_EXISTING)) {
                return;
            }
            const HANDLE file = OpenLogFile(path_);
            if (file == INVALID_HANDLE_VALUE) return;
            fileHandle_ = file;
            writtenBytes_ = 0;
        }
        DWORD written = 0;
        if (!WriteFile(
                static_cast<HANDLE>(fileHandle_),
                line.data(),
                static_cast<DWORD>(line.size()),
                &written,
                nullptr) || written != line.size() ||
            !FlushFileBuffers(static_cast<HANDLE>(fileHandle_))) {
            CloseHandle(static_cast<HANDLE>(fileHandle_));
            fileHandle_ = nullptr;
        } else {
            writtenBytes_ += written;
        }
    } catch (...) {
        // Diagnostics must never affect companion availability.
    }
}

} // namespace luvletter::indexer
