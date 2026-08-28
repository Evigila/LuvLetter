#include "luvletter/indexing/FileIndex.h"
#include "luvletter/indexing/IndexProtocol.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <array>
#include <cstddef>
#include <filesystem>
#include <iostream>
#include <span>
#include <string>

namespace {

class TemporaryDirectory final {
public:
    TemporaryDirectory() {
        std::array<wchar_t, MAX_PATH> temporaryRoot{};
        GetTempPathW(static_cast<DWORD>(temporaryRoot.size()), temporaryRoot.data());
        path_ = std::filesystem::path(temporaryRoot.data()) /
            (L"LuvLetter.IndexKernel.Tests." + std::to_wstring(GetCurrentProcessId()) + L"." +
             std::to_wstring(GetTickCount64()));
        std::filesystem::create_directories(path_);
    }

    ~TemporaryDirectory() {
        std::error_code ignored;
        std::filesystem::remove_all(path_, ignored);
    }

    [[nodiscard]] const std::filesystem::path& Path() const noexcept { return path_; }

private:
    std::filesystem::path path_;
};

bool CreateEmptyFile(const std::filesystem::path& path) {
    const HANDLE file = CreateFileW(
        path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    CloseHandle(file);
    return true;
}

int failures = 0;

void Expect(const bool condition, const wchar_t* message) {
    if (!condition) {
        std::wcerr << L"FAILED: " << message << L'\n';
        ++failures;
    }
}

void TestProtocolHeaderRoundTrip() {
    using namespace luvletter::indexing::protocol;
    const FrameHeader expected{kMagic, kMajorVersion, MessageType::Query, 42, 0x1020304050607080ULL};
    const auto encoded = EncodeHeader(expected);
    FrameHeader actual{};
    Expect(encoded.size() == kHeaderSize, L"protocol header must remain exactly 20 bytes");
    Expect(DecodeHeader(encoded, actual), L"protocol header should decode");
    Expect(actual.magic == expected.magic && actual.majorVersion == expected.majorVersion &&
        actual.type == expected.type && actual.payloadLength == expected.payloadLength &&
        actual.requestId == expected.requestId,
        L"protocol header round-trip should preserve all fields");
}

void TestStatusPayloadRoundTrip() {
    using namespace luvletter::indexing::protocol;
    const IndexStatus expected{42, true};
    const auto encoded = EncodeStatus(expected);
    IndexStatus actual{};
    Expect(encoded.size() == 9, L"status payload must remain exactly 9 bytes");
    Expect(DecodeStatus(encoded, actual), L"status payload should decode");
    Expect(actual.generation == expected.generation && actual.rebuilding == expected.rebuilding,
        L"status payload round-trip should preserve generation and rebuilding state");

    auto malformed = encoded;
    malformed.back() = std::byte{2};
    Expect(!DecodeStatus(malformed, actual), L"status payload should reject rebuilding values other than zero or one");
}

void TestIndexBuildQueryAndPersistence() {
    TemporaryDirectory temporary;
    const auto nested = temporary.Path() / L"aaa";
    std::filesystem::create_directories(nested);
    Expect(CreateEmptyFile(nested / L"README.md"), L"README.md test fixture should be created");
    Expect(CreateEmptyFile(nested / L"readme1.md"), L"readme1.md test fixture should be created");
    Expect(CreateEmptyFile(nested / L"Résumé.txt"), L"Unicode test fixture should be created");
    Expect(CreateEmptyFile(nested / L"unrelated.bin"), L"unrelated test fixture should be created");

    const std::array roots{temporary.Path(), nested};
    const auto snapshot = luvletter::indexing::IndexBuilder::Build(roots);
    Expect(snapshot != nullptr, L"index should build");
    if (!snapshot) {
        return;
    }
    Expect(snapshot->FileCount() == 4, L"a nested configured root must not duplicate files covered by its parent root");

    const auto matches = snapshot->Query(L"RE", 5);
    Expect(matches.size() == 2, L"ASCII query should match case-insensitively");
    if (matches.size() == 2) {
        Expect(matches[0].displayName == L"README.md", L"results should have stable filename ordering");
        Expect(matches[1].displayName == L"readme1.md", L"second stable result should be readme1.md");
        Expect(std::filesystem::exists(matches[0].fullPath), L"full path should be reconstructed lazily and correctly");
    }

    const auto unicodeMatches = snapshot->Query(L"rÉ", 5);
    Expect(unicodeMatches.size() == 1 && unicodeMatches[0].displayName == L"Résumé.txt",
        L"Unicode query should match case-insensitively");

    const auto limited = snapshot->Query(L"r", 1);
    Expect(limited.size() == 1, L"query must honor maximum result count");
    const auto originalStableId = limited.empty() ? 0 : limited[0].stableId;

    const auto snapshotPath = temporary.Path() / L"cache" / L"file-index-v2.bin";
    Expect(snapshot->Save(snapshotPath), L"snapshot should save atomically");
    const auto restored = luvletter::indexing::IndexSnapshot::Load(snapshotPath);
    Expect(restored != nullptr, L"valid snapshot should load");
    if (restored) {
        const auto restoredMatches = restored->Query(L"r", 1);
        Expect(restoredMatches.size() == 1 && restoredMatches[0].stableId == originalStableId,
            L"restored index should preserve stable ids and query behavior");
    }
}

} // namespace

int wmain() {
    TestProtocolHeaderRoundTrip();
    TestStatusPayloadRoundTrip();
    TestIndexBuildQueryAndPersistence();
    if (failures == 0) {
        std::wcout << L"All LuvLetter.IndexKernel tests passed.\n";
    }
    return failures == 0 ? 0 : 1;
}
