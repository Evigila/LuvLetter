#include "input/FeaturePager.h"

#include <algorithm>

void FeaturePager::Reset(std::size_t itemCount, std::size_t itemsPerPage) noexcept
{
	itemCount_ = itemCount;
	itemsPerPage_ = (std::max)(std::size_t{ 1 }, itemsPerPage);
	currentPage_ = 0;
}

void FeaturePager::SetItemsPerPage(std::size_t itemsPerPage) noexcept
{
	itemsPerPage_ = (std::max)(std::size_t{ 1 }, itemsPerPage);
	ClampCurrentPage();
}

bool FeaturePager::Move(int direction) noexcept
{
	const auto pageCount = PageCount();
	if (pageCount <= 1) return false;

	if (direction < 0)
	{
		currentPage_ = currentPage_ == 0 ? pageCount - 1 : currentPage_ - 1;
	}
	else
	{
		currentPage_ = (currentPage_ + 1) % pageCount;
	}
	return true;
}

std::size_t FeaturePager::CurrentPage() const noexcept
{
	return currentPage_;
}

std::size_t FeaturePager::PageCount() const noexcept
{
	return itemCount_ == 0 ? 0 : ((itemCount_ - 1) / itemsPerPage_) + 1;
}

std::size_t FeaturePager::CurrentItemCount() const noexcept
{
	const auto firstItem = FirstItemIndex();
	return firstItem >= itemCount_
		? 0
		: (std::min)(itemsPerPage_, itemCount_ - firstItem);
}

std::size_t FeaturePager::FirstItemIndex() const noexcept
{
	return currentPage_ * itemsPerPage_;
}

bool FeaturePager::TryResolveIndex(
	std::size_t indexOnPage,
	std::size_t& absoluteIndex) const noexcept
{
	if (indexOnPage >= CurrentItemCount()) return false;
	absoluteIndex = FirstItemIndex() + indexOnPage;
	return true;
}

void FeaturePager::ClampCurrentPage() noexcept
{
	const auto pageCount = PageCount();
	currentPage_ = pageCount == 0 ? 0 : (std::min)(currentPage_, pageCount - 1);
}
