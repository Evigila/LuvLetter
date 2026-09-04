#pragma once

#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string_view>

namespace luvletter::indexer {

// Explicitly enabled, bounded diagnostic output for the companion process.
// Indexing remains fully functional when the log cannot be created or written.
class DiagnosticLog final {
public:
    ~DiagnosticLog();
    DiagnosticLog(const DiagnosticLog&) = delete;
    DiagnosticLog& operator=(const DiagnosticLog&) = delete;

    [[nodiscard]] static std::unique_ptr<DiagnosticLog> Open(
        const std::filesystem::path& path) noexcept;

    void Write(
        std::wstring_view event,
        std::wstring_view detail = {}) noexcept;

private:
    explicit DiagnosticLog(std::filesystem::path path) noexcept;

    std::mutex mutex_;
    std::filesystem::path path_;
    void* fileHandle_ = nullptr;
    std::uint64_t writtenBytes_ = 0;
};

} // namespace luvletter::indexer
