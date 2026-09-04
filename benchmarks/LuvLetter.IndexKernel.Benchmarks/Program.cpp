#include "luvletter/indexing/FileIndex.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <Psapi.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cwchar>
#include <iostream>
#include <limits>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;
using luvletter::indexing::IndexSnapshot;
using luvletter::indexing::SearchResultKind;

constexpr std::uint32_t NoParent = (std::numeric_limits<std::uint32_t>::max)();

struct MemorySample final {
    std::uint64_t workingSetBytes = 0;
    std::uint64_t privateBytes = 0;
};

MemorySample ReadMemory() noexcept {
    PROCESS_MEMORY_COUNTERS_EX counters{};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(
            GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
            sizeof(counters))) {
        return {};
    }
    return {
        static_cast<std::uint64_t>(counters.WorkingSetSize),
        static_cast<std::uint64_t>(counters.PrivateUsage),
    };
}

std::wstring EntityName(const std::size_t index) {
    wchar_t buffer[32]{};
    _snwprintf_s(buffer, _TRUNCATE, L"item%012zu.txt", index);
    return buffer;
}

std::shared_ptr<const IndexSnapshot> CreateSnapshot(const std::size_t entityCount) {
    std::vector<IndexSnapshot::DirectoryRecord> directories;
    std::vector<IndexSnapshot::EntityRecord> entities;
    std::vector<wchar_t> pool;
    directories.reserve(1);
    entities.reserve(entityCount);

    constexpr std::wstring_view root = L"C:\\benchmark";
    const auto rootOffset = static_cast<std::uint32_t>(pool.size());
    pool.insert(pool.end(), root.begin(), root.end());
    directories.push_back({NoParent, rootOffset, static_cast<std::uint32_t>(root.size())});

    constexpr std::size_t nameLength = 20;
    if (entityCount > ((std::numeric_limits<std::uint32_t>::max)() - pool.size()) / nameLength) {
        return {};
    }
    pool.reserve(pool.size() + entityCount * nameLength);
    for (std::size_t index = 0; index < entityCount; ++index) {
        const auto name = EntityName(index);
        const auto nameOffset = static_cast<std::uint32_t>(pool.size());
        pool.insert(pool.end(), name.begin(), name.end());
        const auto stableId = static_cast<std::uint64_t>(index) + 1;
        entities.push_back({
            0,
            nameOffset,
            static_cast<std::uint32_t>(name.size()),
            static_cast<std::uint32_t>(stableId),
            static_cast<std::uint32_t>(stableId >> 32U),
            static_cast<std::uint32_t>(SearchResultKind::File),
        });
    }

    return std::make_shared<const IndexSnapshot>(
        std::move(directories),
        std::move(entities),
        std::move(pool),
        0, luvletter::indexing::CreateIndexBaseIdentity(), 0);
}

double Percentile(const std::vector<double>& sorted, const double percentile) {
    if (sorted.empty()) {
        return 0;
    }
    const auto position = percentile * static_cast<double>(sorted.size() - 1);
    return sorted[static_cast<std::size_t>(position)];
}

std::size_t ParseCount(const wchar_t* value, const std::size_t fallback) {
    if (value == nullptr || *value == L'\0') {
        return fallback;
    }
    wchar_t* end = nullptr;
    const auto parsed = _wcstoui64(value, &end, 10);
    if (end == value || *end != L'\0' || parsed == 0 || parsed > 10'000'000) {
        return fallback;
    }
    return static_cast<std::size_t>(parsed);
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    const auto entityCount = ParseCount(argc > 1 ? argv[1] : nullptr, 100'000);
    const auto iterations = ParseCount(argc > 2 ? argv[2] : nullptr, 2'000);
    const std::wstring query = argc > 3 ? argv[3] : L"item0000000";

    const auto memoryBefore = ReadMemory();
    const auto buildStarted = Clock::now();
    const auto snapshot = CreateSnapshot(entityCount);
    const auto buildFinished = Clock::now();
    if (!snapshot || snapshot->EntityCount() != entityCount) {
        std::wcerr << L"Unable to construct the synthetic snapshot.\n";
        return 1;
    }
    const auto memoryAfter = ReadMemory();

    for (int warmup = 0; warmup < 100; ++warmup) {
        (void)snapshot->Query(query, 5);
    }

    std::vector<double> microseconds;
    microseconds.reserve(iterations);
    std::size_t resultCount = 0;
    for (std::size_t iteration = 0; iteration < iterations; ++iteration) {
        const auto started = Clock::now();
        const auto results = snapshot->Query(query, 5);
        const auto finished = Clock::now();
        resultCount = results.size();
        microseconds.push_back(
            std::chrono::duration<double, std::micro>(finished - started).count());
    }
    std::sort(microseconds.begin(), microseconds.end());

    const auto buildMilliseconds =
        std::chrono::duration<double, std::milli>(buildFinished - buildStarted).count();
    std::wcout
        << L"entities=" << entityCount << L'\n'
        << L"iterations=" << iterations << L'\n'
        << L"query=" << query << L'\n'
        << L"results=" << resultCount << L'\n'
        << L"synthetic_build_ms=" << buildMilliseconds << L'\n'
        << L"query_p50_us=" << Percentile(microseconds, 0.50) << L'\n'
        << L"query_p95_us=" << Percentile(microseconds, 0.95) << L'\n'
        << L"query_p99_us=" << Percentile(microseconds, 0.99) << L'\n'
        << L"working_set_before_bytes=" << memoryBefore.workingSetBytes << L'\n'
        << L"working_set_after_bytes=" << memoryAfter.workingSetBytes << L'\n'
        << L"private_before_bytes=" << memoryBefore.privateBytes << L'\n'
        << L"private_after_bytes=" << memoryAfter.privateBytes << L'\n';
    return 0;
}
