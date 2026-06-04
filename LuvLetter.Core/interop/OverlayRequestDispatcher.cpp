#include "interop/OverlayRequestDispatcher.h"

void OverlayRequestDispatcher::BindWindow(HWND hwnd)
{
	std::lock_guard lock(mutex_);
	hwnd_ = hwnd;
}

void OverlayRequestDispatcher::UnbindWindow()
{
	std::lock_guard lock(mutex_);
	hwnd_ = nullptr;
	queue_.clear();
}

bool OverlayRequestDispatcher::Enqueue(OverlayRequest request)
{
	HWND targetWindow = nullptr;
	{
		std::lock_guard lock(mutex_);
		if (hwnd_ == nullptr)
		{
			return false;
		}

		targetWindow = hwnd_;
		queue_.push_back(std::move(request));
	}

	PostMessageW(targetWindow, RequestMessageId, 0, 0);
	return true;
}

std::vector<OverlayRequest> OverlayRequestDispatcher::Drain()
{
	std::vector<OverlayRequest> requests;
	std::lock_guard lock(mutex_);
	while (!queue_.empty())
	{
		requests.push_back(std::move(queue_.front()));
		queue_.pop_front();
	}

	return requests;
}
