#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace luvletter::indexing {

struct SearchResult final {
    std::uint64_t stableId = 0;
    std::wstring displayName;
    std::wstring fullPath;
};

class IndexSnapshot final {
public:
    struct DirectoryRecord final {
        std::uint32_t parentIndex;
        std::uint32_t nameOffset;
        std::uint32_t nameLength;
    };

    struct FileRecord final {
        std::uint32_t directoryIndex;
        std::uint32_t nameOffset;
        std::uint32_t nameLength;
        std::uint32_t stableIdLow;
        std::uint32_t stableIdHigh;

        [[nodiscard]] std::uint64_t StableId() const noexcept {
            return static_cast<std::uint64_t>(stableIdLow) |
                static_cast<std::uint64_t>(stableIdHigh) << 32U;
        }
    };

    IndexSnapshot() = default;
    IndexSnapshot(
        std::vector<DirectoryRecord> directories,
        std::vector<FileRecord> files,
        std::vector<wchar_t> stringPool);

    [[nodiscard]] std::vector<SearchResult> Query(std::wstring_view query, std::size_t maximumResults) const;
    [[nodiscard]] bool Save(const std::filesystem::path& filePath) const;
    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> Load(const std::filesystem::path& filePath);

    [[nodiscard]] std::size_t FileCount() const noexcept { return files_.size(); }
    [[nodiscard]] std::size_t DirectoryCount() const noexcept { return directories_.size(); }

private:
    [[nodiscard]] std::wstring_view PoolString(std::uint32_t offset, std::uint32_t length) const noexcept;
    [[nodiscard]] std::wstring ReconstructPath(const FileRecord& record) const;

    std::vector<DirectoryRecord> directories_;
    std::vector<FileRecord> files_;
    std::vector<wchar_t> stringPool_;
};

class IndexBuilder final {
public:
    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> Build(
        std::span<const std::filesystem::path> roots,
        const std::atomic_bool* cancellation = nullptr);
};

[[nodiscard]] std::wstring Utf8ToWide(std::string_view text);
[[nodiscard]] std::string WideToUtf8(std::wstring_view text);

} // namespace luvletter::indexing
