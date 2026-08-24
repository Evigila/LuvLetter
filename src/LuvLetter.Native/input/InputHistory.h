#pragma once

#include <cstddef>
#include <string>
#include <vector>

// Owns bounded command history and its draft-aware navigation state. The class
// is independent from Win32 input, rendering, and the Native UI thread.
class InputHistory final
{
public:
	explicit InputHistory(std::size_t capacity = 100) noexcept;

	void Record(const std::wstring& value);
	void ResetNavigation() noexcept;
	bool TryNavigate(int direction, std::wstring& text);

private:
	std::vector<std::wstring> entries_;
	std::wstring draft_;
	std::size_t capacity_;
	int navigationIndex_ = -1;
};
