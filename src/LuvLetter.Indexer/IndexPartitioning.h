#pragma once

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <limits>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace luvletter::indexer {

enum class PartitionMaintenanceTier : std::uint32_t { StartupCritical = 0, Normal = 1 };

struct IndexPartitionDescriptor final {
    std::string id;
    std::filesystem::path root;
    std::vector<std::filesystem::path> delegatedSubtrees;
    PartitionMaintenanceTier tier = PartitionMaintenanceTier::Normal;
    std::chrono::seconds refreshAge{1800};
    std::chrono::seconds automaticGap{60};
};

[[nodiscard]] inline bool IsValidPartitionId(const std::string_view id) noexcept {
    const auto alphaNumeric = [](const char value) {
        return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
    };
    return !id.empty() && id.size() <= 128 && alphaNumeric(id.front()) && alphaNumeric(id.back())
        && std::all_of(id.begin(), id.end(), [&](const char value) {
            return alphaNumeric(value) || value == '.' || value == '_' || value == ':' || value == '-';
        });
}

[[nodiscard]] inline std::wstring NormalizePartitionPath(const std::filesystem::path& path) {
    std::error_code error;
    auto value = std::filesystem::absolute(path, error).lexically_normal().make_preferred().native();
    if (error || value.empty()) return {};
    const auto rootLength = std::filesystem::path(value).root_path().native().size();
    while (value.size() > rootLength && value.back() == L'\\') value.pop_back();
    return value;
}

[[nodiscard]] inline bool PartitionContainsPath(
    const std::filesystem::path& root, const std::filesystem::path& path) {
    const auto parent = NormalizePartitionPath(root);
    const auto candidate = NormalizePartitionPath(path);
    if (parent.empty() || candidate.empty() || candidate.size() < parent.size()) return false;
    if (CompareStringOrdinal(parent.data(), static_cast<int>(parent.size()), candidate.data(),
            static_cast<int>(parent.size()), TRUE) != CSTR_EQUAL) return false;
    return candidate.size() == parent.size() || parent.back() == L'\\' || candidate[parent.size()] == L'\\';
}

[[nodiscard]] inline bool SamePartitionPath(
    const std::filesystem::path& left, const std::filesystem::path& right) {
    return PartitionContainsPath(left, right) && PartitionContainsPath(right, left);
}

[[nodiscard]] inline bool ValidatePartitionTopology(
    const std::span<const IndexPartitionDescriptor> partitions) {
    if (partitions.empty()) return false;
    for (std::size_t index = 0; index < partitions.size(); ++index) {
        const auto& partition = partitions[index];
        if (!IsValidPartitionId(partition.id) || partition.root.empty() || !partition.root.is_absolute() ||
            partition.refreshAge < std::chrono::seconds(1) ||
            partition.automaticGap < std::chrono::seconds(1) ||
            (partition.tier != PartitionMaintenanceTier::StartupCritical &&
                partition.tier != PartitionMaintenanceTier::Normal)) return false;
        for (std::size_t otherIndex = 0; otherIndex < index; ++otherIndex) {
            const auto& other = partitions[otherIndex];
            if (partition.id == other.id || SamePartitionPath(partition.root, other.root)) return false;
        }
        for (const auto& delegated : partition.delegatedSubtrees) {
            if (!delegated.is_absolute() || !PartitionContainsPath(partition.root, delegated) ||
                SamePartitionPath(partition.root, delegated)) return false;
            const bool owned = std::any_of(partitions.begin(), partitions.end(), [&](const auto& candidate) {
                return &candidate != &partition && SamePartitionPath(candidate.root, delegated);
            });
            if (!owned) return false;
        }
        for (const auto& child : partitions) {
            if (&child == &partition || !PartitionContainsPath(partition.root, child.root)) continue;
            const bool delegated = std::any_of(partition.delegatedSubtrees.begin(),
                partition.delegatedSubtrees.end(), [&](const auto& path) {
                    return SamePartitionPath(path, child.root);
                });
            if (!delegated) return false;
        }
    }
    return true;
}

[[nodiscard]] inline const IndexPartitionDescriptor* ResolvePartitionOwner(
    const std::span<const IndexPartitionDescriptor> partitions,
    const std::filesystem::path& path) {
    const IndexPartitionDescriptor* owner = nullptr;
    std::size_t ownerLength = 0;
    for (const auto& partition : partitions) {
        if (PartitionContainsPath(partition.root, path)) {
            const auto length = NormalizePartitionPath(partition.root).size();
            if (owner == nullptr || length > ownerLength ||
                (length == ownerLength && partition.id < owner->id)) {
                owner = &partition;
                ownerLength = length;
            }
        }
    }
    return owner;
}

struct PartitionSchedulingState final {
    const IndexPartitionDescriptor* descriptor = nullptr;
    std::chrono::steady_clock::time_point due{};
    std::chrono::steady_clock::time_point dirtySince{};
    std::chrono::steady_clock::time_point lastServiced{};
    std::chrono::seconds estimatedCost{1};
};

[[nodiscard]] inline double PartitionSchedulingPriority(
    const PartitionSchedulingState& state, const std::chrono::steady_clock::time_point now) noexcept {
    if (state.descriptor == nullptr) return -(std::numeric_limits<double>::max)();
    const auto age = [now](const auto since) {
        if (since == std::chrono::steady_clock::time_point{} || since >= now) return 0.0;
        return (std::min)(1440.0,
            std::chrono::duration<double, std::ratio<60>>(now - since).count());
    };
    const auto startup = state.descriptor->tier == PartitionMaintenanceTier::StartupCritical ? 1000.0 : 0.0;
    const auto overdue = state.due < now ? age(state.due) : 0.0;
    const auto dirty = age(state.dirtySince);
    const auto starvation = state.lastServiced == std::chrono::steady_clock::time_point{} ? 1440.0 : age(state.lastServiced);
    const auto cost = (std::min)(1440.0, static_cast<double>((std::max)(state.estimatedCost,
        std::chrono::seconds::zero()).count()) / 60.0);
    const auto score = startup + overdue + dirty + starvation - cost;
    return std::isfinite(score) ? score : 0.0;
}

[[nodiscard]] inline std::string PartitionCacheName(const std::string_view id) {
    // FNV-1a keeps cache names fixed and avoids treating logical separators as paths.
    std::uint64_t hash = 14695981039346656037ULL;
    for (const unsigned char value : id) { hash ^= value; hash *= 1099511628211ULL; }
    constexpr char digits[] = "0123456789abcdef";
    std::string result = "partition-";
    for (int shift = 60; shift >= 0; shift -= 4) result.push_back(digits[(hash >> shift) & 0xFU]);
    return result + ".bin";
}

} // namespace luvletter::indexer
