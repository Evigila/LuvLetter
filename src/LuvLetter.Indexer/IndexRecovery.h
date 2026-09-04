#pragma once

#include "luvletter/indexing/FileIndex.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <vector>

namespace luvletter::indexer {

enum class DeltaOperationKind : std::uint8_t {
    Upsert = 1,
    Remove = 2,
    RemoveTree = 3,
};

// A journal operation contains everything needed to rebuild one in-memory Delta
// entry. Paths are persisted as UTF-16 and never depend on a directory table owned
// by the immutable base snapshot.
struct DeltaOperation final {
    DeltaOperationKind kind = DeltaOperationKind::Upsert;
    indexing::SearchResultKind entryKind = indexing::SearchResultKind::File;
    std::uint64_t stableId = 0;
    std::uint64_t rootId = 0;
    std::filesystem::path path;
};

struct DeltaBatch final {
    std::uint64_t sequence = 0;
    std::vector<DeltaOperation> operations;
};

// A journal can be replayed only over the exact immutable base and enumeration
// policy that produced it. baseAppliedSequence lets a compacted snapshot continue
// the monotonic batch sequence without replaying retired operations.
struct RecoveryJournalBinding final {
    indexing::IndexBaseIdentity baseIdentity{};
    std::uint64_t rootsFingerprint = 0;
    std::uint64_t policyFingerprint = 0;
    std::uint64_t baseAppliedSequence = 0;
};

enum class RecoveryJournalOpenStatus {
    Ready,
    Missing,
    PartialTail,
    Corrupt,
    BindingMismatch,
    IoError,
};

// Append operations are serialized and flushed one complete batch at a time. The
// class deliberately does not mutate LiveIndexDelta yet; integration decides
// whether a detected partial tail can be repaired or requires a full rebuild.
class RecoveryJournal final {
public:
    ~RecoveryJournal();
    RecoveryJournal(const RecoveryJournal&) = delete;
    RecoveryJournal& operator=(const RecoveryJournal&) = delete;

    [[nodiscard]] static std::unique_ptr<RecoveryJournal> Create(
        const std::filesystem::path& path,
        const RecoveryJournalBinding& binding);

    // Complete batches before a partial tail are returned for diagnostics and safe
    // replay, but no appendable journal is returned until the caller resets it.
    [[nodiscard]] static std::unique_ptr<RecoveryJournal> Open(
        const std::filesystem::path& path,
        const RecoveryJournalBinding& expectedBinding,
        std::vector<DeltaBatch>& replay,
        RecoveryJournalOpenStatus& status);

    [[nodiscard]] bool AppendAndFlush(const DeltaBatch& batch);

    // Writes and flushes a new header beside the journal, then atomically replaces
    // the old file. The previous journal remains intact if replacement fails.
    [[nodiscard]] bool Reset(const RecoveryJournalBinding& binding);

    [[nodiscard]] RecoveryJournalBinding Binding() const;
    [[nodiscard]] std::uint64_t NextSequence() const;

private:
    RecoveryJournal(
        std::filesystem::path path,
        RecoveryJournalBinding binding,
        void* fileHandle,
        std::uint64_t nextSequence);

    mutable std::mutex mutex_;
    std::filesystem::path path_;
    RecoveryJournalBinding binding_;
    void* fileHandle_ = nullptr;
    std::uint64_t nextSequence_ = 0;
    bool faulted_ = false;
};

} // namespace luvletter::indexer
