#include "IndexRecovery.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <bit>
#include <cstring>
#include <limits>
#include <span>
#include <string>
#include <system_error>
#include <utility>

namespace luvletter::indexer {
namespace {

static_assert(sizeof(wchar_t) == 2, "Recovery journal paths require Windows UTF-16 wchar_t.");

constexpr std::uint32_t kJournalMagic = 0x4A524C4C; // LLRJ
constexpr std::uint32_t kBatchMagic = 0x42524C4C; // LLRB
constexpr std::uint16_t kJournalVersion = 1;
constexpr std::uint16_t kJournalHeaderSize = 64;
constexpr std::uint16_t kBatchVersion = 1;
constexpr std::uint16_t kBatchHeaderSize = 40;
constexpr std::size_t kOperationHeaderSize = 24;
constexpr std::size_t kMaximumOperationsPerBatch = 4096;
constexpr std::size_t kMaximumPathCharacters = 32767;
constexpr std::size_t kMaximumBatchPayloadBytes = 16U * 1024U * 1024U;
constexpr std::uint64_t kHashOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kHashPrime = 1099511628211ULL;

using ByteVector = std::vector<std::byte>;

enum class ReadResult {
    Complete,
    End,
    Partial,
    Error,
};

void AppendU16(ByteVector& bytes, const std::uint16_t value) {
    bytes.push_back(static_cast<std::byte>(value & 0xFFU));
    bytes.push_back(static_cast<std::byte>((value >> 8U) & 0xFFU));
}

void AppendU32(ByteVector& bytes, const std::uint32_t value) {
    for (unsigned shift = 0; shift < 32; shift += 8) {
        bytes.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

void AppendU64(ByteVector& bytes, const std::uint64_t value) {
    for (unsigned shift = 0; shift < 64; shift += 8) {
        bytes.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

bool ReadU16(
    const std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint16_t& value) {
    if (cursor > bytes.size() || bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[cursor])) |
        static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[cursor + 1])) << 8U;
    cursor += sizeof(value);
    return true;
}

bool ReadU32(
    const std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint32_t& value) {
    if (cursor > bytes.size() || bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = 0;
    for (unsigned shift = 0; shift < 32; shift += 8) {
        value |= static_cast<std::uint32_t>(
            std::to_integer<std::uint8_t>(bytes[cursor++])) << shift;
    }
    return true;
}

bool ReadU64(
    const std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint64_t& value) {
    if (cursor > bytes.size() || bytes.size() - cursor < sizeof(value)) {
        return false;
    }
    value = 0;
    for (unsigned shift = 0; shift < 64; shift += 8) {
        value |= static_cast<std::uint64_t>(
            std::to_integer<std::uint8_t>(bytes[cursor++])) << shift;
    }
    return true;
}

std::uint64_t HashBytes(
    const std::span<const std::byte> bytes,
    std::uint64_t hash = kHashOffsetBasis) noexcept {
    for (const auto value : bytes) {
        hash ^= std::to_integer<std::uint8_t>(value);
        hash *= kHashPrime;
    }
    return hash;
}

bool SameBinding(
    const RecoveryJournalBinding& left,
    const RecoveryJournalBinding& right) noexcept {
    return left.baseIdentity.high == right.baseIdentity.high &&
        left.baseIdentity.low == right.baseIdentity.low &&
        left.rootsFingerprint == right.rootsFingerprint &&
        left.policyFingerprint == right.policyFingerprint &&
        left.baseAppliedSequence == right.baseAppliedSequence;
}

ByteVector EncodeJournalHeader(const RecoveryJournalBinding& binding) {
    ByteVector bytes;
    bytes.reserve(kJournalHeaderSize);
    AppendU32(bytes, kJournalMagic);
    AppendU16(bytes, kJournalVersion);
    AppendU16(bytes, kJournalHeaderSize);
    AppendU64(bytes, binding.baseIdentity.high);
    AppendU64(bytes, binding.baseIdentity.low);
    AppendU64(bytes, binding.rootsFingerprint);
    AppendU64(bytes, binding.policyFingerprint);
    AppendU64(bytes, binding.baseAppliedSequence);
    AppendU64(bytes, 0);
    AppendU64(bytes, HashBytes(bytes));
    return bytes;
}

bool DecodeJournalHeader(
    const std::span<const std::byte> bytes,
    RecoveryJournalBinding& binding) {
    if (bytes.size() != kJournalHeaderSize) {
        return false;
    }

    std::size_t cursor = 0;
    std::uint32_t magic = 0;
    std::uint16_t version = 0;
    std::uint16_t headerSize = 0;
    std::uint64_t reserved = 0;
    std::uint64_t checksum = 0;
    const bool valid = ReadU32(bytes, cursor, magic) &&
        ReadU16(bytes, cursor, version) &&
        ReadU16(bytes, cursor, headerSize) &&
        ReadU64(bytes, cursor, binding.baseIdentity.high) &&
        ReadU64(bytes, cursor, binding.baseIdentity.low) &&
        ReadU64(bytes, cursor, binding.rootsFingerprint) &&
        ReadU64(bytes, cursor, binding.policyFingerprint) &&
        ReadU64(bytes, cursor, binding.baseAppliedSequence) &&
        ReadU64(bytes, cursor, reserved) &&
        ReadU64(bytes, cursor, checksum);
    return valid && cursor == bytes.size() &&
        magic == kJournalMagic && version == kJournalVersion &&
        headerSize == kJournalHeaderSize && reserved == 0 &&
        checksum == HashBytes(bytes.first(bytes.size() - sizeof(checksum)));
}

bool IsValidOperation(const DeltaOperation& operation) {
    if (operation.kind != DeltaOperationKind::Upsert &&
        operation.kind != DeltaOperationKind::Remove &&
        operation.kind != DeltaOperationKind::RemoveTree) {
        return false;
    }
    if (operation.entryKind != indexing::SearchResultKind::File &&
        operation.entryKind != indexing::SearchResultKind::Directory) {
        return false;
    }
    const auto path = operation.path.native();
    return !path.empty() && path.size() <= kMaximumPathCharacters &&
        path.find(L'\0') == std::wstring::npos;
}

bool EncodeBatch(const DeltaBatch& batch, ByteVector& encoded) {
    if (batch.sequence == 0 || batch.operations.empty() ||
        batch.operations.size() > kMaximumOperationsPerBatch) {
        return false;
    }

    ByteVector payload;
    for (const auto& operation : batch.operations) {
        if (!IsValidOperation(operation)) {
            return false;
        }
        const auto path = operation.path.native();
        const auto pathBytes = path.size() * sizeof(wchar_t);
        if (pathBytes > kMaximumBatchPayloadBytes ||
            payload.size() > kMaximumBatchPayloadBytes - kOperationHeaderSize ||
            pathBytes > kMaximumBatchPayloadBytes - payload.size() - kOperationHeaderSize) {
            return false;
        }

        payload.push_back(static_cast<std::byte>(
            static_cast<std::uint8_t>(operation.kind)));
        payload.push_back(static_cast<std::byte>(
            static_cast<std::uint8_t>(operation.entryKind)));
        AppendU16(payload, 0);
        AppendU32(payload, static_cast<std::uint32_t>(path.size()));
        AppendU64(payload, operation.stableId);
        AppendU64(payload, operation.rootId);
        const auto* pathData = reinterpret_cast<const std::byte*>(path.data());
        payload.insert(payload.end(), pathData, pathData + pathBytes);
    }

    encoded.clear();
    encoded.reserve(kBatchHeaderSize + payload.size());
    AppendU32(encoded, kBatchMagic);
    AppendU16(encoded, kBatchVersion);
    AppendU16(encoded, kBatchHeaderSize);
    AppendU64(encoded, batch.sequence);
    AppendU32(encoded, static_cast<std::uint32_t>(batch.operations.size()));
    AppendU32(encoded, 0);
    AppendU64(encoded, payload.size());
    const auto checksum = HashBytes(payload, HashBytes(encoded));
    AppendU64(encoded, checksum);
    encoded.insert(encoded.end(), payload.begin(), payload.end());
    return true;
}

bool DecodeBatch(
    const std::span<const std::byte> header,
    const std::span<const std::byte> payload,
    DeltaBatch& batch) {
    if (header.size() != kBatchHeaderSize || payload.size() > kMaximumBatchPayloadBytes) {
        return false;
    }

    std::size_t cursor = 0;
    std::uint32_t magic = 0;
    std::uint16_t version = 0;
    std::uint16_t headerSize = 0;
    std::uint32_t operationCount = 0;
    std::uint32_t reserved = 0;
    std::uint64_t payloadLength = 0;
    std::uint64_t checksum = 0;
    if (!ReadU32(header, cursor, magic) ||
        !ReadU16(header, cursor, version) ||
        !ReadU16(header, cursor, headerSize) ||
        !ReadU64(header, cursor, batch.sequence) ||
        !ReadU32(header, cursor, operationCount) ||
        !ReadU32(header, cursor, reserved) ||
        !ReadU64(header, cursor, payloadLength) ||
        !ReadU64(header, cursor, checksum) ||
        cursor != header.size() || magic != kBatchMagic ||
        version != kBatchVersion || headerSize != kBatchHeaderSize ||
        reserved != 0 || batch.sequence == 0 || operationCount == 0 ||
        operationCount > kMaximumOperationsPerBatch || payloadLength != payload.size() ||
        checksum != HashBytes(payload, HashBytes(header.first(header.size() - sizeof(checksum))))) {
        return false;
    }

    batch.operations.clear();
    batch.operations.reserve(operationCount);
    cursor = 0;
    for (std::uint32_t index = 0; index < operationCount; ++index) {
        if (payload.size() - cursor < kOperationHeaderSize) {
            return false;
        }
        const auto operationKind = static_cast<DeltaOperationKind>(
            std::to_integer<std::uint8_t>(payload[cursor++]));
        const auto entryKind = static_cast<indexing::SearchResultKind>(
            std::to_integer<std::uint8_t>(payload[cursor++]));
        std::uint16_t operationReserved = 0;
        std::uint32_t pathCharacters = 0;
        std::uint64_t stableId = 0;
        std::uint64_t rootId = 0;
        if (!ReadU16(payload, cursor, operationReserved) ||
            !ReadU32(payload, cursor, pathCharacters) ||
            !ReadU64(payload, cursor, stableId) ||
            !ReadU64(payload, cursor, rootId) || operationReserved != 0 ||
            pathCharacters == 0 || pathCharacters > kMaximumPathCharacters) {
            return false;
        }
        const auto pathBytes = static_cast<std::size_t>(pathCharacters) * sizeof(wchar_t);
        if (payload.size() - cursor < pathBytes) {
            return false;
        }

        std::wstring path(pathCharacters, L'\0');
        std::memcpy(path.data(), payload.data() + cursor, pathBytes);
        cursor += pathBytes;
        DeltaOperation operation{
            operationKind,
            entryKind,
            stableId,
            rootId,
            std::filesystem::path(std::move(path))};
        if (!IsValidOperation(operation)) {
            return false;
        }
        batch.operations.push_back(std::move(operation));
    }
    return cursor == payload.size();
}

bool WriteAll(const HANDLE file, const std::span<const std::byte> bytes) {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        const auto remaining = bytes.size() - offset;
        const auto requested = static_cast<DWORD>(std::min<std::size_t>(
            remaining,
            (std::numeric_limits<DWORD>::max)()));
        DWORD written = 0;
        if (!WriteFile(file, bytes.data() + offset, requested, &written, nullptr) || written == 0) {
            return false;
        }
        offset += written;
    }
    return true;
}

ReadResult ReadExact(const HANDLE file, const std::span<std::byte> bytes) {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        const auto remaining = bytes.size() - offset;
        const auto requested = static_cast<DWORD>(std::min<std::size_t>(
            remaining,
            (std::numeric_limits<DWORD>::max)()));
        DWORD read = 0;
        if (!ReadFile(file, bytes.data() + offset, requested, &read, nullptr)) {
            return ReadResult::Error;
        }
        if (read == 0) {
            return offset == 0 ? ReadResult::End : ReadResult::Partial;
        }
        offset += read;
    }
    return ReadResult::Complete;
}

HANDLE OpenJournalFile(const std::filesystem::path& path, const DWORD creationDisposition) {
    return CreateFileW(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr,
        creationDisposition,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
}

bool EnsureParentDirectory(const std::filesystem::path& path) {
    const auto parent = path.parent_path();
    if (parent.empty()) {
        return true;
    }
    std::error_code error;
    std::filesystem::create_directories(parent, error);
    return !error;
}

bool WriteFreshFile(
    const std::filesystem::path& path,
    const RecoveryJournalBinding& binding,
    const DWORD creationDisposition) {
    const HANDLE file = OpenJournalFile(path, creationDisposition);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    const auto header = EncodeJournalHeader(binding);
    const bool succeeded = WriteAll(file, header) && FlushFileBuffers(file);
    CloseHandle(file);
    if (!succeeded && creationDisposition == CREATE_NEW) {
        DeleteFileW(path.c_str());
    }
    return succeeded;
}

std::filesystem::path TemporaryResetPath(const std::filesystem::path& path) {
    auto temporary = path;
    temporary += L".reset." + std::to_wstring(GetCurrentProcessId()) + L"." +
        std::to_wstring(GetTickCount64());
    return temporary;
}

std::uint64_t FirstSequence(const RecoveryJournalBinding& binding) noexcept {
    return binding.baseAppliedSequence == (std::numeric_limits<std::uint64_t>::max)()
        ? 0
        : binding.baseAppliedSequence + 1;
}

} // namespace

RecoveryJournal::RecoveryJournal(
    std::filesystem::path path,
    const RecoveryJournalBinding binding,
    void* const fileHandle,
    const std::uint64_t nextSequence)
    : path_(std::move(path)),
      binding_(binding),
      fileHandle_(fileHandle),
      nextSequence_(nextSequence) {}

RecoveryJournal::~RecoveryJournal() {
    if (fileHandle_ != nullptr) {
        CloseHandle(static_cast<HANDLE>(fileHandle_));
    }
}

std::unique_ptr<RecoveryJournal> RecoveryJournal::Create(
    const std::filesystem::path& path,
    const RecoveryJournalBinding& binding) {
    if (!EnsureParentDirectory(path) || !WriteFreshFile(path, binding, CREATE_NEW)) {
        return {};
    }
    const HANDLE file = OpenJournalFile(path, OPEN_EXISTING);
    if (file == INVALID_HANDLE_VALUE) {
        return {};
    }
    LARGE_INTEGER end{};
    if (!SetFilePointerEx(file, end, nullptr, FILE_END)) {
        CloseHandle(file);
        return {};
    }
    return std::unique_ptr<RecoveryJournal>(new RecoveryJournal(
        path,
        binding,
        file,
        FirstSequence(binding)));
}

std::unique_ptr<RecoveryJournal> RecoveryJournal::Open(
    const std::filesystem::path& path,
    const RecoveryJournalBinding& expectedBinding,
    std::vector<DeltaBatch>& replay,
    RecoveryJournalOpenStatus& status) {
    replay.clear();
    const HANDLE file = OpenJournalFile(path, OPEN_EXISTING);
    if (file == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError();
        status = error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND
            ? RecoveryJournalOpenStatus::Missing
            : RecoveryJournalOpenStatus::IoError;
        return {};
    }

    std::array<std::byte, kJournalHeaderSize> header{};
    const auto headerRead = ReadExact(file, header);
    if (headerRead != ReadResult::Complete) {
        CloseHandle(file);
        status = headerRead == ReadResult::Error
            ? RecoveryJournalOpenStatus::IoError
            : RecoveryJournalOpenStatus::PartialTail;
        return {};
    }

    RecoveryJournalBinding binding{};
    if (!DecodeJournalHeader(header, binding)) {
        CloseHandle(file);
        status = RecoveryJournalOpenStatus::Corrupt;
        return {};
    }
    if (!SameBinding(binding, expectedBinding)) {
        CloseHandle(file);
        status = RecoveryJournalOpenStatus::BindingMismatch;
        return {};
    }

    auto nextSequence = FirstSequence(binding);
    for (;;) {
        std::array<std::byte, kBatchHeaderSize> batchHeader{};
        const auto batchHeaderRead = ReadExact(file, batchHeader);
        if (batchHeaderRead == ReadResult::End) {
            status = RecoveryJournalOpenStatus::Ready;
            return std::unique_ptr<RecoveryJournal>(new RecoveryJournal(
                path,
                binding,
                file,
                nextSequence));
        }
        if (batchHeaderRead != ReadResult::Complete) {
            CloseHandle(file);
            status = batchHeaderRead == ReadResult::Error
                ? RecoveryJournalOpenStatus::IoError
                : RecoveryJournalOpenStatus::PartialTail;
            return {};
        }

        std::size_t cursor = 24;
        std::uint64_t payloadLength = 0;
        if (!ReadU64(batchHeader, cursor, payloadLength) ||
            payloadLength > kMaximumBatchPayloadBytes) {
            replay.clear();
            CloseHandle(file);
            status = RecoveryJournalOpenStatus::Corrupt;
            return {};
        }
        ByteVector payload(static_cast<std::size_t>(payloadLength));
        const auto payloadRead = ReadExact(file, payload);
        if (payloadRead != ReadResult::Complete) {
            CloseHandle(file);
            status = payloadRead == ReadResult::Error
                ? RecoveryJournalOpenStatus::IoError
                : RecoveryJournalOpenStatus::PartialTail;
            return {};
        }

        DeltaBatch batch;
        if (!DecodeBatch(batchHeader, payload, batch) ||
            nextSequence == 0 || batch.sequence != nextSequence) {
            replay.clear();
            CloseHandle(file);
            status = RecoveryJournalOpenStatus::Corrupt;
            return {};
        }
        replay.push_back(std::move(batch));
        nextSequence = nextSequence == (std::numeric_limits<std::uint64_t>::max)()
            ? 0
            : nextSequence + 1;
    }
}

bool RecoveryJournal::AppendAndFlush(const DeltaBatch& batch) {
    std::lock_guard lock(mutex_);
    if (fileHandle_ == nullptr || faulted_ || nextSequence_ == 0 ||
        batch.sequence != nextSequence_) {
        return false;
    }

    ByteVector encoded;
    if (!EncodeBatch(batch, encoded)) {
        return false;
    }
    const auto file = static_cast<HANDLE>(fileHandle_);
    if (!WriteAll(file, encoded) || !FlushFileBuffers(file)) {
        faulted_ = true;
        return false;
    }
    nextSequence_ = nextSequence_ == (std::numeric_limits<std::uint64_t>::max)()
        ? 0
        : nextSequence_ + 1;
    return true;
}

bool RecoveryJournal::Reset(const RecoveryJournalBinding& binding) {
    std::lock_guard lock(mutex_);
    if (!EnsureParentDirectory(path_)) {
        return false;
    }

    const auto temporaryPath = TemporaryResetPath(path_);
    DeleteFileW(temporaryPath.c_str());
    if (!WriteFreshFile(temporaryPath, binding, CREATE_NEW)) {
        return false;
    }

    const auto previousFile = static_cast<HANDLE>(fileHandle_);
    if (previousFile != nullptr) {
        CloseHandle(previousFile);
        fileHandle_ = nullptr;
    }

    if (!MoveFileExW(
            temporaryPath.c_str(),
            path_.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temporaryPath.c_str());
        const HANDLE reopened = OpenJournalFile(path_, OPEN_EXISTING);
        if (reopened != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER end{};
            if (SetFilePointerEx(reopened, end, nullptr, FILE_END)) {
                fileHandle_ = reopened;
            } else {
                CloseHandle(reopened);
            }
        }
        faulted_ = fileHandle_ == nullptr;
        return false;
    }

    const HANDLE replacement = OpenJournalFile(path_, OPEN_EXISTING);
    if (replacement == INVALID_HANDLE_VALUE) {
        faulted_ = true;
        return false;
    }
    LARGE_INTEGER end{};
    if (!SetFilePointerEx(replacement, end, nullptr, FILE_END)) {
        CloseHandle(replacement);
        faulted_ = true;
        return false;
    }

    fileHandle_ = replacement;
    binding_ = binding;
    nextSequence_ = FirstSequence(binding);
    faulted_ = false;
    return true;
}

RecoveryJournalBinding RecoveryJournal::Binding() const {
    std::lock_guard lock(mutex_);
    return binding_;
}

std::uint64_t RecoveryJournal::NextSequence() const {
    std::lock_guard lock(mutex_);
    return nextSequence_;
}

} // namespace luvletter::indexer
