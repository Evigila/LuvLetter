#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>
#include <utility>

namespace luvletter::indexing {

enum class SearchResultKind : std::uint32_t {
    File = 1,
    Directory = 2,
};

enum class SearchMatchQuality : std::uint32_t {
    ExactName = 0,
    ExactStem = 1,
    Prefix = 2,
    None = 0xFFFFFFFFU,
};

struct SearchResult final {
    std::uint64_t stableId = 0;
    SearchResultKind kind = SearchResultKind::File;
    std::wstring displayName;
    std::wstring fullPath;
};

struct IndexBaseIdentity final {
    std::uint64_t high = 0;
    std::uint64_t low = 0;

    [[nodiscard]] bool IsEmpty() const noexcept { return high == 0 && low == 0; }

    friend bool operator==(const IndexBaseIdentity&, const IndexBaseIdentity&) = default;
};

[[nodiscard]] IndexBaseIdentity CreateIndexBaseIdentity() noexcept;

enum class IndexBuildStage : std::uint8_t {
    Scanning,
    Packing,
};

struct IndexBuildProgress final {
    IndexBuildStage stage = IndexBuildStage::Scanning;
    std::uint64_t processedDirectories = 0;
    std::uint64_t pendingDirectories = 0;
    std::uint64_t discoveredEntries = 0;
    std::wstring_view currentPath;
    std::uint32_t errorCode = 0;
    bool rootUnavailable = false;
};

using IndexBuildProgressCallback = std::function<void(const IndexBuildProgress&)>;

struct IndexBuildOptions final {
    std::uint64_t appliedDeltaSequence = 0;
    std::span<const std::filesystem::path> excludedPaths;
    IndexBuildProgressCallback progress;
};

using SearchResultFilter = std::function<bool(const SearchResult&)>;

[[nodiscard]] SearchMatchQuality ClassifySearchMatch(
    std::wstring_view name,
    SearchResultKind kind,
    std::wstring_view query) noexcept;

[[nodiscard]] bool IsBetterSearchResult(
    const SearchResult& left,
    const SearchResult& right,
    std::wstring_view query) noexcept;

// Matches exact paths and directory descendants without accessing the filesystem.
class PathExclusions final {
public:
    explicit PathExclusions(std::span<const std::filesystem::path> paths = {});

    [[nodiscard]] bool Contains(const std::filesystem::path& path) const;
    // The caller guarantees an absolute, lexically-normal path without an
    // extended-length prefix. This avoids repeating normalization in scanners.
    [[nodiscard]] bool ContainsNormalized(const std::filesystem::path& path) const;
    [[nodiscard]] bool Empty() const noexcept { return paths_.empty(); }
    [[nodiscard]] std::span<const std::filesystem::path> Paths() const noexcept { return paths_; }

private:
    std::vector<std::filesystem::path> paths_;
};

class IndexSnapshot final {
public:
    struct DirectoryRecord final {
        std::uint32_t parentIndex;
        std::uint32_t nameOffset;
        std::uint32_t nameLength;
    };

    struct EntityRecord final {
        std::uint32_t directoryIndex;
        std::uint32_t nameOffset;
        std::uint32_t nameLength;
        std::uint32_t stableIdLow;
        std::uint32_t stableIdHigh;
        std::uint32_t kind;

        [[nodiscard]] std::uint64_t StableId() const noexcept {
            return static_cast<std::uint64_t>(stableIdLow) |
                static_cast<std::uint64_t>(stableIdHigh) << 32U;
        }
    };

    IndexSnapshot() = default;
    IndexSnapshot(
        std::vector<DirectoryRecord> directories,
        std::vector<EntityRecord> entities,
        std::vector<wchar_t> stringPool,
        std::uint64_t rootsFingerprint,
        IndexBaseIdentity baseIdentity,
        std::uint64_t appliedDeltaSequence);

    [[nodiscard]] std::vector<SearchResult> Query(std::wstring_view query, std::size_t maximumResults) const;
    [[nodiscard]] std::vector<SearchResult> Query(
        std::wstring_view query,
        std::size_t maximumResults,
        const SearchResultFilter& filter) const;
    // Maintenance-only export used to compact an immutable base with a recovered Delta.
    // Query paths should continue to reconstruct only their bounded result set.
    [[nodiscard]] std::vector<SearchResult> AllResults() const;
    [[nodiscard]] bool Save(const std::filesystem::path& filePath) const;
    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> Load(const std::filesystem::path& filePath);

    [[nodiscard]] bool MatchesRoots(
        std::span<const std::filesystem::path> roots,
        std::span<const std::filesystem::path> fullIgnorePaths = {}) const;

    [[nodiscard]] std::size_t EntityCount() const noexcept { return entities_.size(); }
    [[nodiscard]] std::size_t FileCount() const noexcept;
    [[nodiscard]] std::size_t DirectoryCount() const noexcept { return directories_.size(); }
    [[nodiscard]] std::uint64_t RootsFingerprint() const noexcept { return rootsFingerprint_; }
    [[nodiscard]] IndexBaseIdentity BaseIdentity() const noexcept { return baseIdentity_; }
    [[nodiscard]] std::uint64_t AppliedDeltaSequence() const noexcept { return appliedDeltaSequence_; }

private:
    [[nodiscard]] std::wstring_view PoolString(std::uint32_t offset, std::uint32_t length) const noexcept;
    [[nodiscard]] std::wstring ReconstructPath(const EntityRecord& record) const;

    std::vector<DirectoryRecord> directories_;
    std::vector<EntityRecord> entities_;
    std::vector<wchar_t> stringPool_;
    std::uint64_t rootsFingerprint_ = 0;
    IndexBaseIdentity baseIdentity_{};
    std::uint64_t appliedDeltaSequence_ = 0;
};

class IndexBuilder final {
public:
    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> Build(
        std::span<const std::filesystem::path> roots,
        const std::atomic_bool* cancellation = nullptr,
        std::span<const std::filesystem::path> fullIgnorePaths = {},
        IndexBuildOptions options = {});

    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> Build(
        std::span<const std::filesystem::path> roots,
        const std::atomic_bool* cancellation,
        IndexBuildOptions options) {
        return Build(roots, cancellation, {}, std::move(options));
    }


    [[nodiscard]] static std::shared_ptr<const IndexSnapshot> BuildFromResults(
        std::span<const SearchResult> results,
        std::uint64_t rootsFingerprint,
        IndexBaseIdentity baseIdentity,
        std::uint64_t appliedDeltaSequence);
};

[[nodiscard]] std::wstring Utf8ToWide(std::string_view text);
[[nodiscard]] std::string WideToUtf8(std::wstring_view text);

} // namespace luvletter::indexing
