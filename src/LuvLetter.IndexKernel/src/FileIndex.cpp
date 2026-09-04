#include "luvletter/indexing/FileIndex.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <limits>
#include <optional>
#include <system_error>
#include <utility>

namespace luvletter::indexing {
namespace {

static_assert(sizeof(IndexSnapshot::EntityRecord) == 24, "EntityRecord must remain compact.");

constexpr std::uint32_t kSnapshotMagic = 0x49464C4C; // LLFI
constexpr std::uint16_t kSnapshotVersion = 3;
constexpr std::uint16_t kSnapshotHeaderSize = 80;
constexpr std::uint64_t kPersistedDirectorySize = 12;
constexpr std::uint64_t kPersistedEntitySize = 24;
constexpr std::uint32_t kNoParent = (std::numeric_limits<std::uint32_t>::max)();

struct SnapshotMetadata final {
    std::uint64_t directoryCount = 0;
    std::uint64_t entityCount = 0;
    std::uint64_t poolCharacterCount = 0;
    std::uint64_t directoryBytes = 0;
    std::uint64_t entityBytes = 0;
    std::uint64_t poolBytes = 0;
    std::uint64_t fileLength = 0;
    std::uint64_t rootsFingerprint = 0;
    std::uint64_t payloadChecksum = 0;
};

struct TemporaryDirectory final {
    std::uint32_t parentIndex;
    std::wstring name;
};

struct TemporaryEntity final {
    std::uint32_t directoryIndex;
    std::wstring name;
    std::uint64_t stableId;
    SearchResultKind kind;
};

struct PendingDirectory final {
    std::filesystem::path path;
    std::uint32_t directoryIndex;
};

void AppendU16(std::vector<std::byte>& bytes, const std::uint16_t value) {
    bytes.push_back(static_cast<std::byte>(value & 0xFFU));
    bytes.push_back(static_cast<std::byte>((value >> 8U) & 0xFFU));
}

void AppendU32(std::vector<std::byte>& bytes, const std::uint32_t value) {
    for (unsigned shift = 0; shift < 32; shift += 8) {
        bytes.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

void AppendU64(std::vector<std::byte>& bytes, const std::uint64_t value) {
    for (unsigned shift = 0; shift < 64; shift += 8) {
        bytes.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

bool ReadU16(const std::span<const std::byte> bytes, std::size_t& cursor, std::uint16_t& value) {
    if (bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[cursor])) |
        static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[cursor + 1])) << 8U;
    cursor += sizeof(value);
    return true;
}

bool ReadU32(const std::span<const std::byte> bytes, std::size_t& cursor, std::uint32_t& value) {
    if (bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = 0;
    for (unsigned shift = 0; shift < 32; shift += 8) {
        value |= static_cast<std::uint32_t>(std::to_integer<std::uint8_t>(bytes[cursor++])) << shift;
    }
    return true;
}

bool ReadU64(const std::span<const std::byte> bytes, std::size_t& cursor, std::uint64_t& value) {
    if (bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = 0;
    for (unsigned shift = 0; shift < 64; shift += 8) {
        value |= static_cast<std::uint64_t>(std::to_integer<std::uint8_t>(bytes[cursor++])) << shift;
    }
    return true;
}

bool CheckedAdd(const std::uint64_t left, const std::uint64_t right, std::uint64_t& result) {
    if (right > (std::numeric_limits<std::uint64_t>::max)() - left) {
        return false;
    }
    result = left + right;
    return true;
}

bool CheckedMultiply(const std::uint64_t left, const std::uint64_t right, std::uint64_t& result) {
    if (left != 0 && right > (std::numeric_limits<std::uint64_t>::max)() / left) {
        return false;
    }
    result = left * right;
    return true;
}

std::uint64_t HashBytes(
    const std::span<const std::byte> bytes,
    std::uint64_t hash = 14695981039346656037ULL) noexcept {
    constexpr std::uint64_t prime = 1099511628211ULL;
    for (const auto value : bytes) {
        hash ^= std::to_integer<std::uint8_t>(value);
        hash *= prime;
    }
    return hash;
}

int CompareOrdinal(const std::wstring_view left, const std::wstring_view right, const BOOL ignoreCase) noexcept {
    const int result = CompareStringOrdinal(
        left.data(),
        static_cast<int>(left.size()),
        right.data(),
        static_cast<int>(right.size()),
        ignoreCase);
    if (result == CSTR_LESS_THAN) {
        return -1;
    }
    if (result == CSTR_GREATER_THAN) {
        return 1;
    }
    return 0;
}

int CompareOrdinalIgnoreCase(const std::wstring_view left, const std::wstring_view right) noexcept {
    return CompareOrdinal(left, right, TRUE);
}

bool StartsWithOrdinalIgnoreCase(const std::wstring_view value, const std::wstring_view prefix) noexcept {
    return prefix.size() <= value.size() &&
        CompareStringOrdinal(
            value.data(),
            static_cast<int>(prefix.size()),
            prefix.data(),
            static_cast<int>(prefix.size()),
            TRUE) == CSTR_EQUAL;
}

std::wstring FoldCase(const std::wstring_view value) {
    if (value.empty()) {
        return {};
    }

    std::wstring folded(value.size(), L'\0');
    const int length = LCMapStringEx(
        LOCALE_NAME_INVARIANT,
        LCMAP_LOWERCASE,
        value.data(),
        static_cast<int>(value.size()),
        folded.data(),
        static_cast<int>(folded.size()),
        nullptr,
        nullptr,
        0);
    if (length == 0) {
        folded.assign(value);
    }
    return folded;
}

std::uint64_t HashFoldedText(const std::wstring_view text, std::uint64_t hash) {
    constexpr std::uint64_t prime = 1099511628211ULL;
    const std::wstring folded = FoldCase(text);
    for (const wchar_t character : folded) {
        const auto value = static_cast<std::uint16_t>(character);
        hash ^= value & 0xFFU;
        hash *= prime;
        hash ^= value >> 8U;
        hash *= prime;
    }
    return hash;
}

std::uint64_t StablePathId(const std::wstring_view path) {
    return HashFoldedText(path, 14695981039346656037ULL);
}

std::filesystem::path NormalizePath(const std::filesystem::path& path) {
    std::error_code error;
    auto absolute = std::filesystem::absolute(path, error);
    if (error) {
        absolute = path;
    }
    auto normalized = absolute.lexically_normal();
    std::wstring text = normalized.native();
    while (text.size() > normalized.root_path().native().size() &&
           (text.back() == L'\\' || text.back() == L'/')) {
        text.pop_back();
    }
    return std::filesystem::path(std::move(text));
}

std::filesystem::path NormalizeExclusionPath(const std::filesystem::path& path) {
    std::wstring text = path.native();
    std::replace(text.begin(), text.end(), L'/', L'\\');
    if (StartsWithOrdinalIgnoreCase(text, L"\\\\?\\UNC\\")) {
        text = L"\\\\" + text.substr(8);
    } else if (text.starts_with(L"\\\\?\\") && text.size() >= 7 &&
            text[5] == L':' && text[6] == L'\\') {
        text.erase(0, 4);
    }
    return NormalizePath(std::filesystem::path(std::move(text))).make_preferred();
}

std::filesystem::path ExtendedPath(const std::filesystem::path& path) {
    const std::wstring text = path.native();
    if (text.starts_with(L"\\\\?\\")) {
        return path;
    }
    if (text.starts_with(L"\\\\")) {
        return std::filesystem::path(L"\\\\?\\UNC\\" + text.substr(2));
    }
    return std::filesystem::path(L"\\\\?\\" + text);
}

DWORD AttributesOf(const std::filesystem::path& path) {
    return GetFileAttributesW(ExtendedPath(path).c_str());
}

bool IsExplicitReparseRoot(const std::filesystem::path& path) {
    const DWORD attributes = AttributesOf(path);
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

bool IsDirectoryCoveredBy(const std::filesystem::path& candidate, const std::filesystem::path& retainedRoot) {
    const std::wstring_view candidateText = candidate.native();
    const std::wstring_view rootText = retainedRoot.native();
    if (rootText.empty() || candidateText.size() <= rootText.size() ||
        CompareStringOrdinal(
            candidateText.data(),
            static_cast<int>(rootText.size()),
            rootText.data(),
            static_cast<int>(rootText.size()),
            TRUE) != CSTR_EQUAL) {
        return false;
    }
    const auto isSeparator = [](const wchar_t character) { return character == L'\\' || character == L'/'; };
    return isSeparator(rootText.back()) || isSeparator(candidateText[rootText.size()]);
}

std::vector<std::filesystem::path> NormalizeRoots(
    const std::span<const std::filesystem::path> roots,
    const PathExclusions& exclusions) {
    std::vector<std::filesystem::path> normalized;
    normalized.reserve(roots.size());
    for (const auto& root : roots) {
        if (!root.empty()) {
            normalized.push_back(NormalizePath(root));
        }
    }
    std::sort(normalized.begin(), normalized.end(), [](const auto& left, const auto& right) {
        const int foldedOrder = CompareOrdinalIgnoreCase(left.native(), right.native());
        return foldedOrder != 0 ? foldedOrder < 0 : CompareOrdinal(left.native(), right.native(), FALSE) < 0;
    });
    normalized.erase(
        std::unique(normalized.begin(), normalized.end(), [](const auto& left, const auto& right) {
            return CompareOrdinalIgnoreCase(left.native(), right.native()) == 0;
        }),
        normalized.end());

    std::vector<std::filesystem::path> disjoint;
    disjoint.reserve(normalized.size());
    for (const auto& candidate : normalized) {
        const bool covered = std::any_of(disjoint.begin(), disjoint.end(), [&](const auto& retained) {
                return IsDirectoryCoveredBy(candidate, retained);
            }) && (exclusions.Contains(candidate) || !IsExplicitReparseRoot(candidate));
        if (!covered) {
            disjoint.push_back(candidate);
        }
    }
    return disjoint;
}

std::uint64_t ComputeRootsFingerprint(
    const std::span<const std::filesystem::path> normalizedRoots,
    const PathExclusions& exclusions) {
    constexpr std::uint64_t offsetBasis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    std::uint64_t hash = offsetBasis;
    for (const auto& root : normalizedRoots) {
        hash = HashFoldedText(root.native(), hash);
        hash ^= 0xFFU;
        hash *= prime;
    }
    // Preserve v3 cache compatibility when the full-ignore scope is empty.
    if (!exclusions.Paths().empty()) {
        hash ^= 0xFEU;
        hash *= prime;
        for (const auto& path : exclusions.Paths()) {
            hash = HashFoldedText(path.native(), hash);
            hash ^= 0xFFU;
            hash *= prime;
        }
    }
    return hash;
}

bool IsValidKind(const std::uint32_t kind) noexcept {
    return kind == static_cast<std::uint32_t>(SearchResultKind::File) ||
        kind == static_cast<std::uint32_t>(SearchResultKind::Directory);
}

bool BaseEntityLess(
    const std::wstring_view leftName,
    const SearchResultKind leftKind,
    const std::uint64_t leftStableId,
    const std::wstring_view rightName,
    const SearchResultKind rightKind,
    const std::uint64_t rightStableId) noexcept {
    const int foldedOrder = CompareOrdinalIgnoreCase(leftName, rightName);
    if (foldedOrder != 0) {
        return foldedOrder < 0;
    }
    const int ordinalOrder = CompareOrdinal(leftName, rightName, FALSE);
    if (ordinalOrder != 0) {
        return ordinalOrder < 0;
    }
    if (leftKind != rightKind) {
        return leftKind == SearchResultKind::Directory;
    }
    return leftStableId < rightStableId;
}

bool WriteAll(const HANDLE file, const std::span<const std::byte> bytes) {
    std::size_t cursor = 0;
    while (cursor < bytes.size()) {
        const auto remaining = bytes.size() - cursor;
        const DWORD requested = static_cast<DWORD>((std::min<std::size_t>)(remaining, MAXDWORD));
        DWORD written = 0;
        if (!WriteFile(file, bytes.data() + cursor, requested, &written, nullptr) || written == 0) {
            return false;
        }
        cursor += written;
    }
    return true;
}

bool ReadAll(const HANDLE file, const std::span<std::byte> bytes) {
    std::size_t cursor = 0;
    while (cursor < bytes.size()) {
        const auto remaining = bytes.size() - cursor;
        const DWORD requested = static_cast<DWORD>((std::min<std::size_t>)(remaining, MAXDWORD));
        DWORD read = 0;
        if (!ReadFile(file, bytes.data() + cursor, requested, &read, nullptr) || read == 0) {
            return false;
        }
        cursor += read;
    }
    return true;
}

std::vector<std::byte> EncodeMetadata(const SnapshotMetadata& metadata) {
    std::vector<std::byte> bytes;
    bytes.reserve(kSnapshotHeaderSize);
    AppendU32(bytes, kSnapshotMagic);
    AppendU16(bytes, kSnapshotVersion);
    AppendU16(bytes, kSnapshotHeaderSize);
    AppendU64(bytes, metadata.directoryCount);
    AppendU64(bytes, metadata.entityCount);
    AppendU64(bytes, metadata.poolCharacterCount);
    AppendU64(bytes, metadata.directoryBytes);
    AppendU64(bytes, metadata.entityBytes);
    AppendU64(bytes, metadata.poolBytes);
    AppendU64(bytes, metadata.fileLength);
    AppendU64(bytes, metadata.rootsFingerprint);
    AppendU64(bytes, metadata.payloadChecksum);
    return bytes;
}

bool DecodeMetadata(const std::span<const std::byte> bytes, SnapshotMetadata& metadata) {
    std::size_t cursor = 0;
    std::uint32_t magic = 0;
    std::uint16_t version = 0;
    std::uint16_t headerSize = 0;
    return ReadU32(bytes, cursor, magic) && magic == kSnapshotMagic &&
        ReadU16(bytes, cursor, version) && version == kSnapshotVersion &&
        ReadU16(bytes, cursor, headerSize) && headerSize == kSnapshotHeaderSize &&
        ReadU64(bytes, cursor, metadata.directoryCount) &&
        ReadU64(bytes, cursor, metadata.entityCount) &&
        ReadU64(bytes, cursor, metadata.poolCharacterCount) &&
        ReadU64(bytes, cursor, metadata.directoryBytes) &&
        ReadU64(bytes, cursor, metadata.entityBytes) &&
        ReadU64(bytes, cursor, metadata.poolBytes) &&
        ReadU64(bytes, cursor, metadata.fileLength) &&
        ReadU64(bytes, cursor, metadata.rootsFingerprint) &&
        ReadU64(bytes, cursor, metadata.payloadChecksum) &&
        cursor == bytes.size();
}

} // namespace

PathExclusions::PathExclusions(const std::span<const std::filesystem::path> paths) {
    paths_.reserve(paths.size());
    for (const auto& path : paths) {
        if (!path.empty()) {
            paths_.push_back(NormalizeExclusionPath(path));
        }
    }
    std::sort(paths_.begin(), paths_.end(), [](const auto& left, const auto& right) {
        const int foldedOrder = CompareOrdinalIgnoreCase(left.native(), right.native());
        return foldedOrder != 0 ? foldedOrder < 0 : CompareOrdinal(left.native(), right.native(), FALSE) < 0;
    });
    paths_.erase(std::unique(paths_.begin(), paths_.end(), [](const auto& left, const auto& right) {
        return CompareOrdinalIgnoreCase(left.native(), right.native()) == 0;
    }), paths_.end());
}

bool PathExclusions::Contains(const std::filesystem::path& path) const {
    if (path.empty() || paths_.empty()) {
        return false;
    }
    const auto normalized = NormalizeExclusionPath(path);
    return std::any_of(paths_.begin(), paths_.end(), [&](const auto& excluded) {
        return CompareOrdinalIgnoreCase(normalized.native(), excluded.native()) == 0 ||
            IsDirectoryCoveredBy(normalized, excluded);
    });
}

SearchMatchQuality ClassifySearchMatch(
    const std::wstring_view name,
    const SearchResultKind kind,
    const std::wstring_view query) noexcept {
    if (name.empty() || query.empty()) {
        return SearchMatchQuality::None;
    }
    if (CompareOrdinalIgnoreCase(name, query) == 0) {
        return SearchMatchQuality::ExactName;
    }
    if (kind == SearchResultKind::File) {
        const auto dot = name.find_last_of(L'.');
        if (dot != std::wstring_view::npos && dot != 0 &&
            CompareOrdinalIgnoreCase(name.substr(0, dot), query) == 0) {
            return SearchMatchQuality::ExactStem;
        }
    }
    return StartsWithOrdinalIgnoreCase(name, query)
        ? SearchMatchQuality::Prefix
        : SearchMatchQuality::None;
}

bool IsBetterSearchResult(
    const SearchResult& left,
    const SearchResult& right,
    const std::wstring_view query) noexcept {
    const auto leftQuality = ClassifySearchMatch(left.displayName, left.kind, query);
    const auto rightQuality = ClassifySearchMatch(right.displayName, right.kind, query);
    if (leftQuality != rightQuality) {
        return static_cast<std::uint32_t>(leftQuality) < static_cast<std::uint32_t>(rightQuality);
    }
    if (BaseEntityLess(
            left.displayName,
            left.kind,
            left.stableId,
            right.displayName,
            right.kind,
            right.stableId)) {
        return true;
    }
    if (BaseEntityLess(
            right.displayName,
            right.kind,
            right.stableId,
            left.displayName,
            left.kind,
            left.stableId)) {
        return false;
    }
    const int pathOrder = CompareOrdinalIgnoreCase(left.fullPath, right.fullPath);
    return pathOrder != 0 ? pathOrder < 0 : CompareOrdinal(left.fullPath, right.fullPath, FALSE) < 0;
}

IndexSnapshot::IndexSnapshot(
    std::vector<DirectoryRecord> directories,
    std::vector<EntityRecord> entities,
    std::vector<wchar_t> stringPool,
    const std::uint64_t rootsFingerprint)
    : directories_(std::move(directories)),
      entities_(std::move(entities)),
      stringPool_(std::move(stringPool)),
      rootsFingerprint_(rootsFingerprint) {}

std::wstring_view IndexSnapshot::PoolString(const std::uint32_t offset, const std::uint32_t length) const noexcept {
    if (offset > stringPool_.size() || length > stringPool_.size() - offset) {
        return {};
    }
    return {stringPool_.data() + offset, length};
}

std::wstring IndexSnapshot::ReconstructPath(const EntityRecord& record) const {
    if (record.directoryIndex >= directories_.size()) {
        return {};
    }

    std::vector<std::wstring_view> components;
    components.reserve(16);
    std::uint32_t current = record.directoryIndex;
    for (std::size_t depth = 0; current != kNoParent && depth <= directories_.size(); ++depth) {
        if (current >= directories_.size()) {
            return {};
        }
        const auto& directory = directories_[current];
        components.push_back(PoolString(directory.nameOffset, directory.nameLength));
        current = directory.parentIndex;
    }
    if (current != kNoParent || components.empty()) {
        return {};
    }

    std::wstring path;
    for (auto iterator = components.rbegin(); iterator != components.rend(); ++iterator) {
        if (!path.empty() && path.back() != L'\\' && path.back() != L'/') {
            path.push_back(L'\\');
        }
        path.append(*iterator);
    }
    if (!path.empty() && path.back() != L'\\' && path.back() != L'/') {
        path.push_back(L'\\');
    }
    path.append(PoolString(record.nameOffset, record.nameLength));
    return path;
}

std::vector<SearchResult> IndexSnapshot::Query(
    const std::wstring_view query,
    const std::size_t maximumResults) const {
    return Query(query, maximumResults, {});
}

std::vector<SearchResult> IndexSnapshot::Query(
    const std::wstring_view query,
    const std::size_t maximumResults,
    const SearchResultFilter& filter) const {
    std::vector<SearchResult> results;
    if (query.empty() || maximumResults == 0 || entities_.empty() || query.size() > INT_MAX) {
        return results;
    }

    const auto first = std::lower_bound(
        entities_.begin(),
        entities_.end(),
        query,
        [this](const EntityRecord& record, const std::wstring_view key) {
            return CompareOrdinalIgnoreCase(PoolString(record.nameOffset, record.nameLength), key) < 0;
        });

    results.reserve((std::min)(maximumResults, static_cast<std::size_t>(64)));
    const auto append = [this, &results, &filter](const EntityRecord& record) {
        auto path = ReconstructPath(record);
        if (path.empty()) {
            return;
        }
        SearchResult result{
            record.StableId(),
            static_cast<SearchResultKind>(record.kind),
            std::wstring(PoolString(record.nameOffset, record.nameLength)),
            std::move(path)};
        if (!filter || filter(result)) {
            results.push_back(std::move(result));
        }
    };

    // Equal names form the first quality tier and are contiguous at the prefix lower bound.
    for (auto iterator = first; iterator != entities_.end() && results.size() < maximumResults; ++iterator) {
        const auto name = PoolString(iterator->nameOffset, iterator->nameLength);
        if (CompareOrdinalIgnoreCase(name, query) != 0) {
            break;
        }
        append(*iterator);
    }

    // Exact stems always begin with "query.". Locating that narrower range independently
    // prevents earlier generic prefix matches from hiding a better stem.
    if (results.size() < maximumResults) {
        std::wstring stemPrefix(query);
        stemPrefix.push_back(L'.');
        const auto firstStem = std::lower_bound(
            entities_.begin(),
            entities_.end(),
            std::wstring_view(stemPrefix),
            [this](const EntityRecord& record, const std::wstring_view key) {
                return CompareOrdinalIgnoreCase(PoolString(record.nameOffset, record.nameLength), key) < 0;
            });
        for (auto iterator = firstStem;
             iterator != entities_.end() && results.size() < maximumResults;
             ++iterator) {
            const auto name = PoolString(iterator->nameOffset, iterator->nameLength);
            if (!StartsWithOrdinalIgnoreCase(name, stemPrefix)) {
                break;
            }
            const auto kind = static_cast<SearchResultKind>(iterator->kind);
            if (ClassifySearchMatch(name, kind, query) == SearchMatchQuality::ExactStem) {
                append(*iterator);
            }
        }
    }

    // Base records already use deterministic ordinal name ordering. After excluding the
    // higher quality tiers, the first remaining prefix records are the generic Top-K.
    for (auto iterator = first; iterator != entities_.end() && results.size() < maximumResults; ++iterator) {
        const auto name = PoolString(iterator->nameOffset, iterator->nameLength);
        if (!StartsWithOrdinalIgnoreCase(name, query)) {
            break;
        }
        const auto kind = static_cast<SearchResultKind>(iterator->kind);
        if (ClassifySearchMatch(name, kind, query) == SearchMatchQuality::Prefix) {
            append(*iterator);
        }
    }
    std::sort(results.begin(), results.end(), [&](const SearchResult& left, const SearchResult& right) {
        return IsBetterSearchResult(left, right, query);
    });
    return results;
}

bool IndexSnapshot::MatchesRoots(
    const std::span<const std::filesystem::path> roots,
    const std::span<const std::filesystem::path> fullIgnorePaths) const {
    const PathExclusions exclusions(fullIgnorePaths);
    const auto normalized = NormalizeRoots(roots, exclusions);
    return rootsFingerprint_ == ComputeRootsFingerprint(normalized, exclusions);
}

std::size_t IndexSnapshot::FileCount() const noexcept {
    return static_cast<std::size_t>(std::count_if(entities_.begin(), entities_.end(), [](const auto& entity) {
        return entity.kind == static_cast<std::uint32_t>(SearchResultKind::File);
    }));
}

bool IndexSnapshot::Save(const std::filesystem::path& filePath) const {
    if (directories_.size() > (std::numeric_limits<std::uint32_t>::max)() ||
        entities_.size() > (std::numeric_limits<std::uint32_t>::max)() ||
        stringPool_.size() > (std::numeric_limits<std::uint32_t>::max)()) {
        return false;
    }

    std::error_code error;
    if (!filePath.parent_path().empty()) {
        std::filesystem::create_directories(filePath.parent_path(), error);
    }
    if (error) {
        return false;
    }

    std::uint64_t directoryBytes = 0;
    std::uint64_t entityBytes = 0;
    std::uint64_t poolBytes = 0;
    std::uint64_t fileLength = kSnapshotHeaderSize;
    if (!CheckedMultiply(directories_.size(), kPersistedDirectorySize, directoryBytes) ||
        !CheckedMultiply(entities_.size(), kPersistedEntitySize, entityBytes) ||
        !CheckedMultiply(stringPool_.size(), sizeof(wchar_t), poolBytes) ||
        !CheckedAdd(fileLength, directoryBytes, fileLength) ||
        !CheckedAdd(fileLength, entityBytes, fileLength) ||
        !CheckedAdd(fileLength, poolBytes, fileLength)) {
        return false;
    }

    std::vector<std::byte> records;
    records.reserve(static_cast<std::size_t>(directoryBytes + entityBytes));
    for (const auto& directory : directories_) {
        AppendU32(records, directory.parentIndex);
        AppendU32(records, directory.nameOffset);
        AppendU32(records, directory.nameLength);
    }
    for (const auto& entity : entities_) {
        AppendU32(records, entity.directoryIndex);
        AppendU32(records, entity.nameOffset);
        AppendU32(records, entity.nameLength);
        AppendU32(records, entity.stableIdLow);
        AppendU32(records, entity.stableIdHigh);
        AppendU32(records, entity.kind);
    }

    auto temporaryPath = filePath;
    temporaryPath += L".tmp." + std::to_wstring(GetCurrentProcessId());
    const HANDLE handle = CreateFileW(
        temporaryPath.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return false;
    }

    const auto poolBytesView = std::span{
        reinterpret_cast<const std::byte*>(stringPool_.data()),
        stringPool_.size() * sizeof(wchar_t)};
    const auto checksum = HashBytes(poolBytesView, HashBytes(records));
    const SnapshotMetadata metadata{
        directories_.size(),
        entities_.size(),
        stringPool_.size(),
        directoryBytes,
        entityBytes,
        poolBytes,
        fileLength,
        rootsFingerprint_,
        checksum};
    const auto header = EncodeMetadata(metadata);
    const bool succeeded = WriteAll(handle, header) &&
        WriteAll(handle, records) &&
        WriteAll(handle, poolBytesView) &&
        FlushFileBuffers(handle);
    CloseHandle(handle);

    if (!succeeded || !MoveFileExW(
            temporaryPath.c_str(),
            filePath.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temporaryPath.c_str());
        return false;
    }
    return true;
}

std::shared_ptr<const IndexSnapshot> IndexSnapshot::Load(const std::filesystem::path& filePath) {
    const HANDLE handle = CreateFileW(
        filePath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return {};
    }

    LARGE_INTEGER size{};
    std::array<std::byte, kSnapshotHeaderSize> headerBytes{};
    SnapshotMetadata metadata{};
    bool valid = GetFileSizeEx(handle, &size) && size.QuadPart >= kSnapshotHeaderSize &&
        ReadAll(handle, headerBytes) && DecodeMetadata(headerBytes, metadata);

    std::uint64_t expectedDirectoryBytes = 0;
    std::uint64_t expectedEntityBytes = 0;
    std::uint64_t expectedPoolBytes = 0;
    std::uint64_t expectedLength = kSnapshotHeaderSize;
    valid = valid &&
        CheckedMultiply(metadata.directoryCount, kPersistedDirectorySize, expectedDirectoryBytes) &&
        CheckedMultiply(metadata.entityCount, kPersistedEntitySize, expectedEntityBytes) &&
        CheckedMultiply(metadata.poolCharacterCount, sizeof(wchar_t), expectedPoolBytes) &&
        CheckedAdd(expectedLength, expectedDirectoryBytes, expectedLength) &&
        CheckedAdd(expectedLength, expectedEntityBytes, expectedLength) &&
        CheckedAdd(expectedLength, expectedPoolBytes, expectedLength) &&
        metadata.directoryBytes == expectedDirectoryBytes &&
        metadata.entityBytes == expectedEntityBytes &&
        metadata.poolBytes == expectedPoolBytes &&
        metadata.fileLength == expectedLength &&
        size.QuadPart >= 0 && static_cast<std::uint64_t>(size.QuadPart) == expectedLength &&
        metadata.directoryCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        metadata.entityCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        metadata.poolCharacterCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        expectedDirectoryBytes <= (std::numeric_limits<std::size_t>::max)() &&
        expectedEntityBytes <= (std::numeric_limits<std::size_t>::max)() - expectedDirectoryBytes &&
        expectedPoolBytes <= (std::numeric_limits<std::size_t>::max)();
    if (!valid) {
        CloseHandle(handle);
        return {};
    }

    std::vector<std::byte> recordBytes(static_cast<std::size_t>(expectedDirectoryBytes + expectedEntityBytes));
    std::vector<wchar_t> stringPool(static_cast<std::size_t>(metadata.poolCharacterCount));
    valid = ReadAll(handle, recordBytes) &&
        ReadAll(handle, std::span{
            reinterpret_cast<std::byte*>(stringPool.data()),
            stringPool.size() * sizeof(wchar_t)});
    CloseHandle(handle);
    const auto poolBytesView = std::span{
        reinterpret_cast<const std::byte*>(stringPool.data()),
        stringPool.size() * sizeof(wchar_t)};
    if (!valid || HashBytes(poolBytesView, HashBytes(recordBytes)) != metadata.payloadChecksum) {
        return {};
    }

    std::vector<DirectoryRecord> directories;
    std::vector<EntityRecord> entities;
    directories.reserve(static_cast<std::size_t>(metadata.directoryCount));
    entities.reserve(static_cast<std::size_t>(metadata.entityCount));
    std::size_t cursor = 0;
    for (std::uint64_t index = 0; index < metadata.directoryCount; ++index) {
        DirectoryRecord directory{};
        valid = ReadU32(recordBytes, cursor, directory.parentIndex) &&
            ReadU32(recordBytes, cursor, directory.nameOffset) &&
            ReadU32(recordBytes, cursor, directory.nameLength) &&
            directory.nameOffset <= stringPool.size() &&
            directory.nameLength <= stringPool.size() - directory.nameOffset &&
            (directory.parentIndex == kNoParent || directory.parentIndex < index);
        if (!valid) {
            return {};
        }
        directories.push_back(directory);
    }
    for (std::uint64_t index = 0; index < metadata.entityCount; ++index) {
        EntityRecord entity{};
        valid = ReadU32(recordBytes, cursor, entity.directoryIndex) &&
            ReadU32(recordBytes, cursor, entity.nameOffset) &&
            ReadU32(recordBytes, cursor, entity.nameLength) &&
            ReadU32(recordBytes, cursor, entity.stableIdLow) &&
            ReadU32(recordBytes, cursor, entity.stableIdHigh) &&
            ReadU32(recordBytes, cursor, entity.kind) &&
            entity.directoryIndex < directories.size() &&
            entity.nameOffset <= stringPool.size() &&
            entity.nameLength <= stringPool.size() - entity.nameOffset &&
            IsValidKind(entity.kind);
        if (!valid) {
            return {};
        }
        entities.push_back(entity);
    }
    if (cursor != recordBytes.size()) {
        return {};
    }

    for (std::size_t index = 1; index < entities.size(); ++index) {
        const auto& previous = entities[index - 1];
        const auto& current = entities[index];
        const auto previousName = std::wstring_view{
            stringPool.data() + previous.nameOffset, previous.nameLength};
        const auto currentName = std::wstring_view{
            stringPool.data() + current.nameOffset, current.nameLength};
        if (BaseEntityLess(
                currentName,
                static_cast<SearchResultKind>(current.kind),
                current.StableId(),
                previousName,
                static_cast<SearchResultKind>(previous.kind),
                previous.StableId())) {
            return {};
        }
    }

    return std::make_shared<const IndexSnapshot>(
        std::move(directories),
        std::move(entities),
        std::move(stringPool),
        metadata.rootsFingerprint);
}

std::shared_ptr<const IndexSnapshot> IndexBuilder::Build(
    const std::span<const std::filesystem::path> roots,
    const std::atomic_bool* cancellation,
    const std::span<const std::filesystem::path> fullIgnorePaths) {
    const PathExclusions exclusions(fullIgnorePaths);
    const auto normalizedRoots = NormalizeRoots(roots, exclusions);
    const auto rootsFingerprint = ComputeRootsFingerprint(normalizedRoots, exclusions);

    std::vector<TemporaryDirectory> directories;
    std::vector<TemporaryEntity> entities;
    std::vector<PendingDirectory> pending;

    for (const auto& root : normalizedRoots) {
        if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
            return {};
        }
        if (exclusions.Contains(root)) {
            continue;
        }
        const DWORD rootAttributes = AttributesOf(root);
        if (rootAttributes == INVALID_FILE_ATTRIBUTES ||
            (rootAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
            return {};
        }
        if (directories.size() >= (std::numeric_limits<std::uint32_t>::max)()) {
            return {};
        }
        const auto rootIndex = static_cast<std::uint32_t>(directories.size());
        directories.push_back(TemporaryDirectory{kNoParent, root.native()});
        pending.push_back(PendingDirectory{root, rootIndex});
    }

    while (!pending.empty()) {
        if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
            return {};
        }
        PendingDirectory current = std::move(pending.back());
        pending.pop_back();

        auto searchPath = ExtendedPath(current.path);
        searchPath /= L"*";
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileExW(
            searchPath.c_str(),
            FindExInfoBasic,
            &data,
            FindExSearchNameMatch,
            nullptr,
            FIND_FIRST_EX_LARGE_FETCH);
        if (find == INVALID_HANDLE_VALUE && GetLastError() == ERROR_INVALID_PARAMETER) {
            find = FindFirstFileExW(
                searchPath.c_str(),
                FindExInfoBasic,
                &data,
                FindExSearchNameMatch,
                nullptr,
                0);
        }
        if (find == INVALID_HANDLE_VALUE) {
            const DWORD error = GetLastError();
            const bool isRoot = directories[current.directoryIndex].parentIndex == kNoParent;
            if (error == ERROR_FILE_NOT_FOUND) {
                // A wildcard can find no entries in an empty directory. A root
                // that disappeared during enumeration must not replace its cache.
                const DWORD attributes = AttributesOf(current.path);
                if (attributes != INVALID_FILE_ATTRIBUTES &&
                    (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
                    continue;
                }
            }
            if (!isRoot && (error == ERROR_ACCESS_DENIED ||
                    error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)) {
                // Protected or concurrently removed descendants are expected.
                continue;
            }
            return {};
        }

        do {
            if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
                FindClose(find);
                return {};
            }
            const std::wstring_view name(data.cFileName);
            if (name == L"." || name == L".." || name.empty()) {
                continue;
            }
            const auto entryPath = (current.path / name).lexically_normal();
            if (exclusions.Contains(entryPath)) {
                continue;
            }
            if (entities.size() >= (std::numeric_limits<std::uint32_t>::max)()) {
                FindClose(find);
                return {};
            }

            const bool directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (directory) {
                if (directories.size() >= (std::numeric_limits<std::uint32_t>::max)()) {
                    FindClose(find);
                    return {};
                }
                const auto directoryIndex = static_cast<std::uint32_t>(directories.size());
                directories.push_back(TemporaryDirectory{current.directoryIndex, std::wstring(name)});
                entities.push_back(TemporaryEntity{
                    current.directoryIndex,
                    std::wstring(name),
                    StablePathId(entryPath.native()),
                    SearchResultKind::Directory});
                if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0) {
                    pending.push_back(PendingDirectory{entryPath, directoryIndex});
                }
            } else {
                entities.push_back(TemporaryEntity{
                    current.directoryIndex,
                    std::wstring(name),
                    StablePathId(entryPath.native()),
                    SearchResultKind::File});
            }
        } while (FindNextFileW(find, &data));
        const DWORD enumerationError = GetLastError();
        FindClose(find);
        if (enumerationError != ERROR_NO_MORE_FILES) {
            return {};
        }
    }

    if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
        return {};
    }
    std::sort(entities.begin(), entities.end(), [](const TemporaryEntity& left, const TemporaryEntity& right) {
        return BaseEntityLess(
            left.name,
            left.kind,
            left.stableId,
            right.name,
            right.kind,
            right.stableId);
    });

    std::vector<wchar_t> stringPool;
    std::vector<IndexSnapshot::DirectoryRecord> packedDirectories;
    std::vector<IndexSnapshot::EntityRecord> packedEntities;
    packedDirectories.reserve(directories.size());
    packedEntities.reserve(entities.size());

    auto addString = [&stringPool](const std::wstring_view value) -> std::optional<std::uint32_t> {
        if (stringPool.size() > (std::numeric_limits<std::uint32_t>::max)() - value.size()) {
            return std::nullopt;
        }
        const auto offset = static_cast<std::uint32_t>(stringPool.size());
        stringPool.insert(stringPool.end(), value.begin(), value.end());
        return offset;
    };

    for (const auto& directory : directories) {
        if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
            return {};
        }
        const auto offset = addString(directory.name);
        if (!offset.has_value()) {
            return {};
        }
        packedDirectories.push_back(IndexSnapshot::DirectoryRecord{
            directory.parentIndex,
            *offset,
            static_cast<std::uint32_t>(directory.name.size())});
    }
    for (const auto& entity : entities) {
        if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
            return {};
        }
        const auto offset = addString(entity.name);
        if (!offset.has_value()) {
            return {};
        }
        packedEntities.push_back(IndexSnapshot::EntityRecord{
            entity.directoryIndex,
            *offset,
            static_cast<std::uint32_t>(entity.name.size()),
            static_cast<std::uint32_t>(entity.stableId),
            static_cast<std::uint32_t>(entity.stableId >> 32U),
            static_cast<std::uint32_t>(entity.kind)});
    }

    if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
        return {};
    }
    return std::make_shared<const IndexSnapshot>(
        std::move(packedDirectories),
        std::move(packedEntities),
        std::move(stringPool),
        rootsFingerprint);
}

std::wstring Utf8ToWide(const std::string_view text) {
    if (text.empty() || text.size() > INT_MAX) {
        return {};
    }
    const int length = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0) {
        return {};
    }
    std::wstring converted(length, L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            text.data(),
            static_cast<int>(text.size()),
            converted.data(),
            length) != length) {
        return {};
    }
    return converted;
}

std::string WideToUtf8(const std::wstring_view text) {
    if (text.empty() || text.size() > INT_MAX) {
        return {};
    }
    const int length = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) {
        return {};
    }
    std::string converted(length, '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            text.data(),
            static_cast<int>(text.size()),
            converted.data(),
            length,
            nullptr,
            nullptr) != length) {
        return {};
    }
    return converted;
}

} // namespace luvletter::indexing
