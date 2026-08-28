#pragma once

#include "api/InputBoxApi.h"

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

struct InputCandidateItem final
{
	uint64_t token = 0;
	LuvLetterCandidateKind kind = LuvLetterCandidateKindFile;
	LuvLetterCandidateIconKind iconKind = LuvLetterCandidateIconKindNone;
	std::wstring primaryText;
	std::wstring secondaryText;
};

struct InputCandidateActivation final
{
	uint64_t token = 0;
	LuvLetterCandidateAction action = LuvLetterCandidateActionOpen;
};

// Pure candidate selection state kept independent from HWND rendering so the
// revision and keyboard-routing contract can be tested without creating a GUI.
class InputCandidateState final
{
public:
	bool Apply(
		std::vector<InputCandidateItem>&& items,
		uint64_t resultRevision,
		uint64_t currentInputRevision)
	{
		if (resultRevision != currentInputRevision)
		{
			return false;
		}

		std::optional<uint64_t> selectedToken;
		if (resultRevision == revision_
			&& selectedIndex_.has_value()
			&& *selectedIndex_ < items_.size())
		{
			selectedToken = items_[*selectedIndex_].token;
		}

		items_ = std::move(items);
		revision_ = resultRevision;
		selectedIndex_.reset();
		if (selectedToken.has_value())
		{
			for (size_t index = 0; index < items_.size(); ++index)
			{
				if (items_[index].token == *selectedToken)
				{
					selectedIndex_ = index;
					break;
				}
			}
		}
		if (!selectedIndex_.has_value() && !items_.empty())
		{
			selectedIndex_ = 0;
		}
		return true;
	}

	void Clear() noexcept
	{
		items_.clear();
		selectedIndex_.reset();
	}

	bool MoveSelection(int direction) noexcept
	{
		if (items_.empty() || direction == 0)
		{
			return false;
		}

		if (!selectedIndex_.has_value())
		{
			selectedIndex_ = direction > 0 ? 0 : items_.size() - 1;
			return true;
		}

		const auto count = items_.size();
		if (direction > 0)
		{
			selectedIndex_ = (*selectedIndex_ + 1) % count;
		}
		else
		{
			selectedIndex_ = *selectedIndex_ == 0
				? count - 1
				: *selectedIndex_ - 1;
		}
		return true;
	}

	bool TryActivate(
		LuvLetterCandidateAction action,
		InputCandidateActivation& activation) const noexcept
	{
		if (!selectedIndex_.has_value() || *selectedIndex_ >= items_.size())
		{
			return false;
		}
		activation = InputCandidateActivation{ items_[*selectedIndex_].token, action };
		return true;
	}

	bool IsEmpty() const noexcept { return items_.empty(); }
	uint64_t Revision() const noexcept { return revision_; }
	const std::vector<InputCandidateItem>& Items() const noexcept { return items_; }
	std::optional<size_t> SelectedIndex() const noexcept { return selectedIndex_; }

private:
	std::vector<InputCandidateItem> items_;
	std::optional<size_t> selectedIndex_;
	uint64_t revision_ = 0;
};
