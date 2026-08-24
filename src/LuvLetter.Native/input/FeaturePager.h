#pragma once

#include <cstddef>

// Owns feature paging state independently from Win32 windowing and rendering.
// All methods are deterministic and do not depend on a UI thread or OS state.
class FeaturePager final
{
public:
	void Reset(std::size_t itemCount, std::size_t itemsPerPage) noexcept;
	void SetItemsPerPage(std::size_t itemsPerPage) noexcept;
	bool Move(int direction) noexcept;

	std::size_t CurrentPage() const noexcept;
	std::size_t PageCount() const noexcept;
	std::size_t CurrentItemCount() const noexcept;
	std::size_t FirstItemIndex() const noexcept;
	bool TryResolveIndex(std::size_t indexOnPage, std::size_t& absoluteIndex) const noexcept;

private:
	void ClampCurrentPage() noexcept;

	std::size_t itemCount_ = 0;
	std::size_t itemsPerPage_ = 1;
	std::size_t currentPage_ = 0;
};
