#pragma once

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cwctype>
#include <filesystem>
#include <limits>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace luvletter::indexer {

enum class RebuildDecision {
    Accepted,
    Ignored,
    Cooldown,
    Capacity,
    InvalidPath,
};

struct RebuildEvaluation {
    RebuildDecision decision;
    std::chrono::seconds remainingCooldownSeconds{0};
};

// Controls change-triggered rebuild requests only. Callers still apply live
// changes and perform scheduled or explicitly requested scans independently.
class IndexRebuildPolicy final {
public:
    using Clock = std::chrono::steady_clock;

    [[nodiscard]] static bool IsValidIgnoredDirectoryName(const std::wstring_view name) {
        if (name.empty() || name.size() > 255 || name == L"." || name == L".." ||
            name.back() == L'.' || name.back() == L' ') {
            return false;
        }
        return std::none_of(name.begin(), name.end(), [](const wchar_t character) {
            return character < 32 || std::wstring_view(L"<>:\"/\\|?*").find(character) != std::wstring_view::npos;
        });
    }

    void Configure(
        const std::vector<std::filesystem::path>& ignoredDirectories,
        const std::chrono::seconds cooldown = std::chrono::seconds(60),
        const std::vector<std::wstring>& ignoredDirectoryNames = {}) {
        std::lock_guard lock(mutex_);
        ignoredDirectories_.clear();
        ignoredDirectories_.reserve(ignoredDirectories.size());
        for (const auto& directory : ignoredDirectories) {
            // Relative scopes would silently change meaning with the working
            // directory. Only absolute directory scopes are supported.
            if (!directory.is_absolute()) {
                continue;
            }
            auto key = NormalizeKey(directory);
            if (!key.empty()) {
                ignoredDirectories_.push_back(std::move(key));
            }
        }
        ignoredDirectoryNames_.clear();
        ignoredDirectoryNames_.reserve(ignoredDirectoryNames.size());
        for (const auto& name : ignoredDirectoryNames) {
            if (IsValidIgnoredDirectoryName(name)) {
                ignoredDirectoryNames_.push_back(FoldCase(name));
            }
        }
        cooldown_ = (std::max)(cooldown, std::chrono::seconds::zero());
        cooldowns_.clear();
        nextExpiration_ = (Clock::time_point::max)();
    }

    [[nodiscard]] RebuildEvaluation Evaluate(
        const std::filesystem::path& path,
        const Clock::time_point now) {
        auto key = NormalizeKey(path);
        if (key.empty()) {
            return {RebuildDecision::InvalidPath};
        }
        std::lock_guard lock(mutex_);
        for (const auto& directory : ignoredDirectories_) {
            if (key == directory ||
                (key.size() > directory.size() && key.starts_with(directory) &&
                    (directory.back() == L'\\' || key[directory.size()] == L'\\'))) {
                return {RebuildDecision::Ignored};
            }
        }
        if (!ignoredDirectoryNames_.empty()) {
            // Exclude the drive/UNC server root, and compare complete components.
            // Including the final component covers creation of an ignored directory.
            const auto relativePath = std::filesystem::path(key).relative_path();
            for (const auto& component : relativePath) {
                if (std::find(ignoredDirectoryNames_.begin(), ignoredDirectoryNames_.end(),
                        component.native()) != ignoredDirectoryNames_.end()) {
                    return {RebuildDecision::Ignored};
                }
            }
        }
        return EvaluateKey(std::move(key), now);
    }

    // A watcher overflow has no trustworthy file path. It shares one bounded
    // cooldown entry and cannot be matched to an ignored directory scope.
    [[nodiscard]] RebuildEvaluation EvaluateUnknown(const Clock::time_point now) {
        std::lock_guard lock(mutex_);
        return EvaluateKey({}, now);
    }

    [[nodiscard]] bool Accept(
        const std::filesystem::path& path,
        const Clock::time_point now) {
        return Evaluate(path, now).decision == RebuildDecision::Accepted;
    }

    [[nodiscard]] bool AcceptUnknown(const Clock::time_point now) {
        return EvaluateUnknown(now).decision == RebuildDecision::Accepted;
    }

private:
    static constexpr std::size_t kMaximumCooldownEntries = 4096;

    [[nodiscard]] static std::wstring NormalizeKey(const std::filesystem::path& path) {
        if (path.empty()) {
            return {};
        }
        auto preferred = path;
        preferred.make_preferred();
        auto input = preferred.native();
        if (input.size() >= 8 &&
            CompareStringOrdinal(input.data(), 8, L"\\\\?\\UNC\\", 8, TRUE) == CSTR_EQUAL) {
            input = L"\\\\" + input.substr(8);
        } else if (input.size() >= 7 && input.starts_with(L"\\\\?\\") &&
            input[5] == L':' && input[6] == L'\\') {
            input.erase(0, 4);
        }
        std::error_code error;
        auto normalized = std::filesystem::absolute(std::filesystem::path(input), error);
        if (error) {
            return {};
        }
        normalized = normalized.lexically_normal().make_preferred();
        auto value = normalized.native();
        const auto rootLength = normalized.root_path().native().size();
        while (value.size() > rootLength && value.back() == L'\\') {
            value.pop_back();
        }
        if (value.empty() || value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
            return {};
        }

        return FoldCase(std::move(value));
    }

    [[nodiscard]] static std::wstring FoldCase(std::wstring value) {
        std::wstring folded(value.size(), L'\0');
        if (LCMapStringEx(
                LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE,
                value.data(), static_cast<int>(value.size()),
                folded.data(), static_cast<int>(folded.size()),
                nullptr, nullptr, 0) == 0) {
            folded = std::move(value);
            std::transform(folded.begin(), folded.end(), folded.begin(), [](const wchar_t character) {
                return static_cast<wchar_t>(std::towlower(character));
            });
        }
        return folded;
    }

    [[nodiscard]] RebuildEvaluation EvaluateKey(std::wstring key, const Clock::time_point now) {
        if (cooldown_ == std::chrono::seconds::zero()) {
            return {RebuildDecision::Accepted};
        }
        if (now >= nextExpiration_) {
            nextExpiration_ = (Clock::time_point::max)();
            std::erase_if(cooldowns_, [this, now](const auto& item) {
                if (item.second <= now) {
                    return true;
                }
                nextExpiration_ = (std::min)(nextExpiration_, item.second);
                return false;
            });
        }
        if (const auto existing = cooldowns_.find(key); existing != cooldowns_.end()) {
            // Suppression never extends a deadline or evicts an active entry;
            // otherwise a burst of new paths could bypass the cooldown.
            return {RebuildDecision::Cooldown,
                std::chrono::ceil<std::chrono::seconds>(existing->second - now)};
        }
        if (cooldowns_.size() >= kMaximumCooldownEntries) {
            return {RebuildDecision::Capacity};
        }
        const auto expires = now + cooldown_;
        cooldowns_.emplace(std::move(key), expires);
        nextExpiration_ = (std::min)(nextExpiration_, expires);
        return {RebuildDecision::Accepted};
    }

    std::mutex mutex_;
    std::vector<std::wstring> ignoredDirectories_;
    std::vector<std::wstring> ignoredDirectoryNames_;
    std::chrono::seconds cooldown_{60};
    std::unordered_map<std::wstring, Clock::time_point> cooldowns_;
    Clock::time_point nextExpiration_ = (Clock::time_point::max)();
};

} // namespace luvletter::indexer
