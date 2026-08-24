#include "input/InputHistory.h"

InputHistory::InputHistory(std::size_t capacity) noexcept
	: capacity_(capacity == 0 ? 1 : capacity)
{
}

void InputHistory::Record(const std::wstring& value)
{
	if (!value.empty() && (entries_.empty() || entries_.back() != value))
	{
		entries_.push_back(value);
		if (entries_.size() > capacity_)
		{
			entries_.erase(entries_.begin());
		}
	}

	ResetNavigation();
}

void InputHistory::ResetNavigation() noexcept
{
	navigationIndex_ = -1;
	draft_.clear();
}

bool InputHistory::TryNavigate(int direction, std::wstring& text)
{
	if (entries_.empty()) return false;
	if (navigationIndex_ < 0)
	{
		if (direction > 0) return false;
		draft_ = text;
		navigationIndex_ = static_cast<int>(entries_.size()) - 1;
	}
	else
	{
		navigationIndex_ += direction;
	}

	if (navigationIndex_ < 0) navigationIndex_ = 0;
	if (navigationIndex_ >= static_cast<int>(entries_.size()))
	{
		navigationIndex_ = -1;
		text = draft_;
		draft_.clear();
	}
	else
	{
		text = entries_[navigationIndex_];
	}
	return true;
}
