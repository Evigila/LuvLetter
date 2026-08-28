#include "luvletter/indexing/FileIndex.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <limits>
#include <optional>
#include <system_error>
#include <unordered_map>
#include <utility>

namespace luvletter::indexing {
namespace {

static_assert(sizeof(IndexSnapshot::FileRecord) == 20, "FileRecord must remain compact.");

constexpr std::uint32_t kSnapshotMagic = 0x49464C4C; // LLFI
constexpr std::uint16_t kSnapshotVersion = 2;
constexpr std::uint16_t kSnapshotHeaderSize = 64;
constexpr std::uint64_t kPersistedDirectorySize = 12;
constexpr std::uint64_t kPersistedFileSize = 20;
constexpr std::uint32_t kNoParent = (std::numeric_limits<std::uint32_t>::max)();

struct SnapshotMetadata final {
    std::uint64_t directoryCount = 0;
    std::uint64_t fileCount = 0;
    std::uint64_t poolCharacterCount = 0;
    std::uint64_t directoryBytes = 0;
    std::uint64_t fileBytes = 0;
    std::uint64_t poolBytes = 0;
    std::uint64_t fileLength = 0;
};

struct TemporaryDirectory final {
    std::uint32_t parentIndex;
    std::wstring name;
};

struct TemporaryFile final {
    std::uint32_t directoryIndex;
    std::wstring name;
    std::uint64_t stableId;
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

int CompareOrdinalIgnoreCase(const std::wstring_view left, const std::wstring_view right) noexcept {
    const int result = CompareStringOrdinal(
        left.data(),
        static_cast<int>(left.size()),
        right.data(),
        static_cast<int>(right.size()),
        TRUE);
    if (result == CSTR_LESS_THAN) {
        return -1;
    }
    if (result == CSTR_GREATER_THAN) {
        return 1;
    }
    return 0;
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

std::uint64_t StablePathId(const std::wstring_view path) {
    constexpr std::uint64_t offsetBasis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    std::uint64_t hash = offsetBasis;
    const std::wstring folded = FoldCase(path);
    for (const wchar_t character : folded) {
        const auto value = static_cast<std::uint16_t>(character);
        hash ^= value & 0xFFU;
        hash *= prime;
        hash ^= value >> 8U;
        hash *= prime;
    }
    return hash;
}

std::filesystem::path NormalizePath(const std::filesystem::path& path) {
    std::error_code error;
    auto absolute = std::filesystem::absolute(path, error);
    if (error) {
        absolute = path;
    }
    return absolute.lexically_normal();
}

bool IsDirectoryCoveredBy(const std::filesystem::path& candidate, const std::filesystem::path& retainedRoot) {
    const std::wstring_view candidateText = candidate.native();
    const std::wstring_view rootText = retainedRoot.native();
    if (candidateText.size() <= rootText.size() ||
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
    AppendU64(bytes, metadata.fileCount);
    AppendU64(bytes, metadata.poolCharacterCount);
    AppendU64(bytes, metadata.directoryBytes);
    AppendU64(bytes, metadata.fileBytes);
    AppendU64(bytes, metadata.poolBytes);
    AppendU64(bytes, metadata.fileLength);
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
        ReadU64(bytes, cursor, metadata.fileCount) &&
        ReadU64(bytes, cursor, metadata.poolCharacterCount) &&
        ReadU64(bytes, cursor, metadata.directoryBytes) &&
        ReadU64(bytes, cursor, metadata.fileBytes) &&
        ReadU64(bytes, cursor, metadata.poolBytes) &&
        ReadU64(bytes, cursor, metadata.fileLength);
}

} // namespace

IndexSnapshot::IndexSnapshot(
    std::vector<DirectoryRecord> directories,
    std::vector<FileRecord> files,
    std::vector<wchar_t> stringPool)
    : directories_(std::move(directories)),
      files_(std::move(files)),
      stringPool_(std::move(stringPool)) {}

std::wstring_view IndexSnapshot::PoolString(const std::uint32_t offset, const std::uint32_t length) const noexcept {
    if (offset > stringPool_.size() || length > stringPool_.size() - offset) {
        return {};
    }
    return {stringPool_.data() + offset, length};
}

std::wstring IndexSnapshot::ReconstructPath(const FileRecord& record) const {
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

std::vector<SearchResult> IndexSnapshot::Query(const std::wstring_view query, const std::size_t maximumResults) const {
    std::vector<SearchResult> results;
    if (query.empty() || maximumResults == 0 || files_.empty() || query.size() > INT_MAX) {
        return results;
    }

    const auto first = std::lower_bound(
        files_.begin(),
        files_.end(),
        query,
        [this](const FileRecord& record, const std::wstring_view key) {
            return CompareOrdinalIgnoreCase(PoolString(record.nameOffset, record.nameLength), key) < 0;
        });

    results.reserve((std::min)(maximumResults, static_cast<std::size_t>(16)));
    for (auto iterator = first; iterator != files_.end() && results.size() < maximumResults; ++iterator) {
        const auto name = PoolString(iterator->nameOffset, iterator->nameLength);
        if (!StartsWithOrdinalIgnoreCase(name, query)) {
            break;
        }

        auto path = ReconstructPath(*iterator);
        if (!path.empty()) {
            results.push_back(SearchResult{iterator->StableId(), std::wstring(name), std::move(path)});
        }
    }
    return results;
}

bool IndexSnapshot::Save(const std::filesystem::path& filePath) const {
    if (directories_.size() > (std::numeric_limits<std::uint32_t>::max)() ||
        files_.size() > (std::numeric_limits<std::uint32_t>::max)() ||
        stringPool_.size() > (std::numeric_limits<std::uint32_t>::max)()) {
        return false;
    }

    std::error_code error;
    std::filesystem::create_directories(filePath.parent_path(), error);
    if (error) {
        return false;
    }

    std::uint64_t directoryBytes = 0;
    std::uint64_t fileBytes = 0;
    std::uint64_t poolBytes = 0;
    std::uint64_t fileLength = kSnapshotHeaderSize;
    if (!CheckedMultiply(directories_.size(), kPersistedDirectorySize, directoryBytes) ||
        !CheckedMultiply(files_.size(), kPersistedFileSize, fileBytes) ||
        !CheckedMultiply(stringPool_.size(), sizeof(wchar_t), poolBytes) ||
        !CheckedAdd(fileLength, directoryBytes, fileLength) ||
        !CheckedAdd(fileLength, fileBytes, fileLength) ||
        !CheckedAdd(fileLength, poolBytes, fileLength)) {
        return false;
    }

    const SnapshotMetadata metadata{
        directories_.size(), files_.size(), stringPool_.size(), directoryBytes, fileBytes, poolBytes, fileLength};
    const auto header = EncodeMetadata(metadata);

    std::vector<std::byte> records;
    records.reserve(static_cast<std::size_t>(directoryBytes + fileBytes));
    for (const auto& directory : directories_) {
        AppendU32(records, directory.parentIndex);
        AppendU32(records, directory.nameOffset);
        AppendU32(records, directory.nameLength);
    }
    for (const auto& file : files_) {
        AppendU32(records, file.directoryIndex);
        AppendU32(records, file.nameOffset);
        AppendU32(records, file.nameLength);
        AppendU32(records, file.stableIdLow);
        AppendU32(records, file.stableIdHigh);
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
    std::uint64_t expectedFileBytes = 0;
    std::uint64_t expectedPoolBytes = 0;
    std::uint64_t expectedLength = kSnapshotHeaderSize;
    valid = valid &&
        CheckedMultiply(metadata.directoryCount, kPersistedDirectorySize, expectedDirectoryBytes) &&
        CheckedMultiply(metadata.fileCount, kPersistedFileSize, expectedFileBytes) &&
        CheckedMultiply(metadata.poolCharacterCount, sizeof(wchar_t), expectedPoolBytes) &&
        CheckedAdd(expectedLength, expectedDirectoryBytes, expectedLength) &&
        CheckedAdd(expectedLength, expectedFileBytes, expectedLength) &&
        CheckedAdd(expectedLength, expectedPoolBytes, expectedLength) &&
        metadata.directoryBytes == expectedDirectoryBytes &&
        metadata.fileBytes == expectedFileBytes &&
        metadata.poolBytes == expectedPoolBytes &&
        metadata.fileLength == expectedLength &&
        size.QuadPart >= 0 && static_cast<std::uint64_t>(size.QuadPart) == expectedLength &&
        metadata.directoryCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        metadata.fileCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        metadata.poolCharacterCount <= (std::numeric_limits<std::uint32_t>::max)() &&
        expectedDirectoryBytes + expectedFileBytes <= (std::numeric_limits<std::size_t>::max)() &&
        expectedPoolBytes <= (std::numeric_limits<std::size_t>::max)();
    if (!valid) {
        CloseHandle(handle);
        return {};
    }

    std::vector<std::byte> recordBytes(static_cast<std::size_t>(expectedDirectoryBytes + expectedFileBytes));
    std::vector<wchar_t> stringPool(static_cast<std::size_t>(metadata.poolCharacterCount));
    valid = ReadAll(handle, recordBytes) &&
        ReadAll(handle, std::span{
            reinterpret_cast<std::byte*>(stringPool.data()),
            stringPool.size() * sizeof(wchar_t)});
    CloseHandle(handle);
    if (!valid) {
        return {};
    }

    std::vector<DirectoryRecord> directories;
    std::vector<FileRecord> files;
    directories.reserve(static_cast<std::size_t>(metadata.directoryCount));
    files.reserve(static_cast<std::size_t>(metadata.fileCount));
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
    for (std::uint64_t index = 0; index < metadata.fileCount; ++index) {
        FileRecord file{};
        valid = ReadU32(recordBytes, cursor, file.directoryIndex) &&
            ReadU32(recordBytes, cursor, file.nameOffset) &&
            ReadU32(recordBytes, cursor, file.nameLength) &&
            ReadU32(recordBytes, cursor, file.stableIdLow) &&
            ReadU32(recordBytes, cursor, file.stableIdHigh) &&
            file.directoryIndex < directories.size() &&
            file.nameOffset <= stringPool.size() &&
            file.nameLength <= stringPool.size() - file.nameOffset;
        if (!valid) {
            return {};
        }
        files.push_back(file);
    }
    if (cursor != recordBytes.size()) {
        return {};
    }

    for (std::size_t index = 1; index < files.size(); ++index) {
        const auto previous = std::wstring_view{
            stringPool.data() + files[index - 1].nameOffset, files[index - 1].nameLength};
        const auto current = std::wstring_view{
            stringPool.data() + files[index].nameOffset, files[index].nameLength};
        if (CompareOrdinalIgnoreCase(previous, current) > 0) {
            return {};
        }
    }

    return std::make_shared<const IndexSnapshot>(
        std::move(directories), std::move(files), std::move(stringPool));
}

std::shared_ptr<const IndexSnapshot> IndexBuilder::Build(
    const std::span<const std::filesystem::path> roots,
    const std::atomic_bool* cancellation) {
    std::vector<std::filesystem::path> normalizedRoots;
    normalizedRoots.reserve(roots.size());
    for (const auto& root : roots) {
        const auto normalized = NormalizePath(root);
        std::error_code error;
        if (std::filesystem::is_directory(normalized, error) && !error) {
            normalizedRoots.push_back(normalized);
        }
    }
    std::sort(normalizedRoots.begin(), normalizedRoots.end(), [](const auto& left, const auto& right) {
        return CompareOrdinalIgnoreCase(left.native(), right.native()) < 0;
    });
    normalizedRoots.erase(
        std::unique(normalizedRoots.begin(), normalizedRoots.end(), [](const auto& left, const auto& right) {
            return CompareOrdinalIgnoreCase(left.native(), right.native()) == 0;
        }),
        normalizedRoots.end());

    std::vector<std::filesystem::path> disjointRoots;
    disjointRoots.reserve(normalizedRoots.size());
    for (const auto& candidate : normalizedRoots) {
        const bool covered = std::any_of(disjointRoots.begin(), disjointRoots.end(), [&](const auto& retained) {
            return IsDirectoryCoveredBy(candidate, retained);
        });
        if (!covered) {
            disjointRoots.push_back(candidate);
        }
    }
    normalizedRoots = std::move(disjointRoots);

    std::vector<TemporaryDirectory> directories;
    std::vector<TemporaryFile> files;
    std::unordered_map<std::wstring, std::uint32_t> directoryByFoldedPath;

    auto addDirectory = [&](const std::filesystem::path& path, const std::uint32_t parent, const bool root) {
        const std::wstring key = FoldCase(path.native());
        const auto existing = directoryByFoldedPath.find(key);
        if (existing != directoryByFoldedPath.end()) {
            return existing->second;
        }
        const auto index = static_cast<std::uint32_t>(directories.size());
        std::wstring name = root ? path.native() : path.filename().native();
        directories.push_back(TemporaryDirectory{parent, std::move(name)});
        directoryByFoldedPath.emplace(std::move(key), index);
        return index;
    };

    for (const auto& root : normalizedRoots) {
        if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
            return {};
        }
        if (directories.size() >= (std::numeric_limits<std::uint32_t>::max)()) {
            return {};
        }

        const std::uint32_t rootIndex = addDirectory(root, kNoParent, true);
        std::error_code error;
        std::filesystem::recursive_directory_iterator iterator(
            root,
            std::filesystem::directory_options::skip_permission_denied,
            error);
        const std::filesystem::recursive_directory_iterator end;
        while (iterator != end) {
            if (cancellation != nullptr && cancellation->load(std::memory_order_relaxed)) {
                return {};
            }
            error.clear();

            const auto entryPath = iterator->path().lexically_normal();
            const bool isDirectory = iterator->is_directory(error);
            if (!error && isDirectory) {
                const auto parentKey = FoldCase(entryPath.parent_path().native());
                const auto parentIterator = directoryByFoldedPath.find(parentKey);
                const auto parent = parentIterator == directoryByFoldedPath.end() ? rootIndex : parentIterator->second;
                if (directories.size() < (std::numeric_limits<std::uint32_t>::max)()) {
                    addDirectory(entryPath, parent, false);
                }
            } else {
                error.clear();
                const bool isFile = iterator->is_regular_file(error);
                if (!error && isFile) {
                    const auto parentKey = FoldCase(entryPath.parent_path().native());
                    const auto parentIterator = directoryByFoldedPath.find(parentKey);
                    const auto parent = parentIterator == directoryByFoldedPath.end() ? rootIndex : parentIterator->second;
                    auto name = entryPath.filename().native();
                    if (!name.empty() && name.size() <= (std::numeric_limits<std::uint32_t>::max)()) {
                        files.push_back(TemporaryFile{
                            parent,
                            std::move(name),
                            StablePathId(entryPath.native())});
                    }
                }
            }

            error.clear();
            iterator.increment(error);
        }
    }

    std::sort(files.begin(), files.end(), [](const TemporaryFile& left, const TemporaryFile& right) {
        const int nameOrder = CompareOrdinalIgnoreCase(left.name, right.name);
        if (nameOrder != 0) {
            return nameOrder < 0;
        }
        if (left.directoryIndex != right.directoryIndex) {
            return left.directoryIndex < right.directoryIndex;
        }
        return left.stableId < right.stableId;
    });

    std::vector<wchar_t> stringPool;
    std::vector<IndexSnapshot::DirectoryRecord> packedDirectories;
    std::vector<IndexSnapshot::FileRecord> packedFiles;
    packedDirectories.reserve(directories.size());
    packedFiles.reserve(files.size());

    auto addString = [&stringPool](const std::wstring_view value) -> std::optional<std::uint32_t> {
        if (stringPool.size() > (std::numeric_limits<std::uint32_t>::max)() - value.size()) {
            return std::nullopt;
        }
        const auto offset = static_cast<std::uint32_t>(stringPool.size());
        stringPool.insert(stringPool.end(), value.begin(), value.end());
        return offset;
    };

    for (const auto& directory : directories) {
        const auto offset = addString(directory.name);
        if (!offset.has_value()) {
            return {};
        }
        packedDirectories.push_back(IndexSnapshot::DirectoryRecord{
            directory.parentIndex, *offset, static_cast<std::uint32_t>(directory.name.size())});
    }
    for (const auto& file : files) {
        const auto offset = addString(file.name);
        if (!offset.has_value()) {
            return {};
        }
        packedFiles.push_back(IndexSnapshot::FileRecord{
            file.directoryIndex,
            *offset,
            static_cast<std::uint32_t>(file.name.size()),
            static_cast<std::uint32_t>(file.stableId),
            static_cast<std::uint32_t>(file.stableId >> 32U)});
    }

    return std::make_shared<const IndexSnapshot>(
        std::move(packedDirectories), std::move(packedFiles), std::move(stringPool));
}

std::wstring Utf8ToWide(const std::string_view text) {
    if (text.empty()) {
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
    if (text.empty()) {
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
