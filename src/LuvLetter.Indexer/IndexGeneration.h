#pragma once
#include "IndexRecovery.h"
#include <Windows.h>
#include <algorithm>
#include <cstddef>
#include <cwctype>
#include <optional>
#include <unordered_map>

namespace luvletter::indexer::recovery {
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

// Change this value whenever enumeration semantics alter which entries belong to a base.
constexpr std::uint64_t kEnumerationPolicyFingerprint = 0x4C55564C45545452ULL;
constexpr std::uint64_t kManifestMagic = 0x34464E414D4C4C49ULL;

struct ActiveIndexManifest final {
    std::uint64_t magic = kManifestMagic;
    std::uint64_t identityHigh = 0;
    std::uint64_t identityLow = 0;
    std::uint64_t checksum = 0;
};
static_assert(sizeof(ActiveIndexManifest) == 32);

inline std::uint64_t HashManifest(const ActiveIndexManifest& manifest) noexcept {
    constexpr std::uint64_t offsetBasis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    auto hash = offsetBasis;
    const auto* bytes = reinterpret_cast<const std::byte*>(&manifest);
    for (std::size_t index = 0; index < offsetof(ActiveIndexManifest, checksum); ++index) {
        hash ^= std::to_integer<std::uint8_t>(bytes[index]);
        hash *= prime;
    }
    return hash;
}

inline std::filesystem::path ManifestPath(const std::filesystem::path& directory) {
    return directory / L"file-index-v4.active";
}

inline std::wstring IdentityStem(const luvletter::indexing::IndexBaseIdentity identity) {
    wchar_t value[48]{};
    swprintf_s(value, L"file-index-v4-%016llx%016llx",
        static_cast<unsigned long long>(identity.high),
        static_cast<unsigned long long>(identity.low));
    return value;
}

inline std::filesystem::path SnapshotPath(
    const std::filesystem::path& directory,
    const luvletter::indexing::IndexBaseIdentity identity) {
    return directory / (IdentityStem(identity) + L".bin");
}

inline std::filesystem::path JournalPath(
    const std::filesystem::path& directory,
    const luvletter::indexing::IndexBaseIdentity identity) {
    return directory / (IdentityStem(identity) + L".delta");
}

inline std::optional<luvletter::indexing::IndexBaseIdentity> LoadManifest(
    const std::filesystem::path& directory, const bool backup = false) {
    auto path = ManifestPath(directory);
    if (backup) path += L".bak";
    UniqueHandle file(CreateFileW(
        path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) {
        return std::nullopt;
    }
    ActiveIndexManifest manifest{};
    DWORD read = 0;
    std::byte trailing{};
    DWORD trailingRead = 0;
    if (!ReadFile(file.Get(), &manifest, sizeof(manifest), &read, nullptr) ||
        read != sizeof(manifest) ||
        !ReadFile(file.Get(), &trailing, 1, &trailingRead, nullptr) ||
        trailingRead != 0 || manifest.magic != kManifestMagic ||
        manifest.checksum != HashManifest(manifest)) {
        return std::nullopt;
    }
    luvletter::indexing::IndexBaseIdentity identity{
        manifest.identityHigh, manifest.identityLow};
    return identity.IsEmpty() ? std::nullopt : std::optional(identity);
}

inline bool SaveManifest(
    const std::filesystem::path& directory,
    const luvletter::indexing::IndexBaseIdentity identity) {
    std::error_code directoryError;
    std::filesystem::create_directories(directory, directoryError);
    if (directoryError) {
        return false;
    }
    auto temporary = ManifestPath(directory);
    temporary += L".tmp";
    DeleteFileW(temporary.c_str());
    UniqueHandle file(CreateFileW(
        temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!file) {
        return false;
    }
    ActiveIndexManifest manifest{};
    manifest.identityHigh = identity.high;
    manifest.identityLow = identity.low;
    manifest.checksum = HashManifest(manifest);
    DWORD written = 0;
    const bool saved = WriteFile(file.Get(), &manifest, sizeof(manifest), &written, nullptr) &&
        written == sizeof(manifest) && FlushFileBuffers(file.Get());
    file.Reset();
    if (saved && LoadManifest(directory)) {
        auto backup = ManifestPath(directory); backup += L".bak";
        auto backupTemporary = backup; backupTemporary += L".tmp";
        if (!CopyFileW(ManifestPath(directory).c_str(), backupTemporary.c_str(), FALSE) ||
            !MoveFileExW(backupTemporary.c_str(), backup.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            DeleteFileW(temporary.c_str());
            DeleteFileW(backupTemporary.c_str());
            return false;
        }
    }
    if (!saved || !MoveFileExW(
            temporary.c_str(), ManifestPath(directory).c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temporary.c_str());
        return false;
    }
    return true;
}

inline std::wstring FoldPath(const std::filesystem::path& path) {
    std::error_code error;
    auto normalized = std::filesystem::absolute(path, error);
    if (error) normalized = path;
    const auto value = normalized.lexically_normal().native();
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
        std::transform(folded.begin(), folded.end(), folded.begin(), [](const wchar_t character) {
            return static_cast<wchar_t>(std::towlower(character));
        });
    }
    return folded;
}

inline std::uint64_t StablePathId(const std::wstring_view path) noexcept {
    constexpr std::uint64_t offsetBasis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    auto hash = offsetBasis;
    for (const wchar_t character : path) {
        const auto value = static_cast<std::uint16_t>(character);
        hash ^= value & 0xFFU;
        hash *= prime;
        hash ^= value >> 8U;
        hash *= prime;
    }
    return hash;
}

inline bool IsSameOrChildPath(
    const std::filesystem::path& candidate,
    const std::filesystem::path& parent) {
    const auto candidateKey = FoldPath(candidate);
    const auto parentKey = FoldPath(parent);
    if (!candidateKey.starts_with(parentKey)) return false;
    if (candidateKey.size() == parentKey.size()) return true;
    const auto last = parentKey.empty() ? L'\0' : parentKey.back();
    const auto next = candidateKey[parentKey.size()];
    return last == L'\\' || last == L'/' || next == L'\\' || next == L'/';
}

inline std::vector<luvletter::indexing::SearchResult> ApplyBatches(
    std::vector<luvletter::indexing::SearchResult> results,
    const std::span<const luvletter::indexer::DeltaBatch> batches,
    const std::uint64_t throughSequence) {
    std::unordered_map<std::wstring, luvletter::indexing::SearchResult> byPath;
    byPath.reserve(results.size());
    for (auto& result : results) {
        byPath.insert_or_assign(FoldPath(result.fullPath), std::move(result));
    }
    for (const auto& batch : batches) {
        if (batch.sequence > throughSequence) {
            continue;
        }
        for (const auto& operation : batch.operations) {
            const auto key = FoldPath(operation.path);
            if (operation.kind == luvletter::indexer::DeltaOperationKind::Remove) {
                byPath.erase(key);
            } else if (operation.kind == luvletter::indexer::DeltaOperationKind::RemoveTree) {
                auto prefix = key;
                if (!prefix.empty() && prefix.back() != L'\\' && prefix.back() != L'/') {
                    prefix += L'\\';
                }
                std::erase_if(byPath, [&prefix](const auto& item) {
                    return item.first.starts_with(prefix);
                });
            } else {
                auto name = operation.path.filename().native();
                if (name.empty()) name = operation.path.native();
                byPath.insert_or_assign(key, luvletter::indexing::SearchResult{
                    operation.stableId,
                    operation.entryKind,
                    std::move(name),
                    operation.path.lexically_normal().native()});
            }
        }
    }
    results.clear();
    results.reserve(byPath.size());
    for (auto& [key, result] : byPath) {
        results.push_back(std::move(result));
    }
    return results;
}


} // namespace luvletter::indexer::recovery
