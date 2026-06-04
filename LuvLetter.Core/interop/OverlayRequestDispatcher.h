#pragma once

#include "interop/OverlayRequest.h"

#include <Windows.h>

#include <deque>
#include <mutex>
#include <vector>

class OverlayRequestDispatcher
{
public:
	static constexpr UINT RequestMessageId = WM_APP + 1;

	void BindWindow(HWND hwnd);
	void UnbindWindow();
	bool Enqueue(OverlayRequest request);
	std::vector<OverlayRequest> Drain();

private:
	std::mutex mutex_;
	HWND hwnd_ = nullptr;
	std::deque<OverlayRequest> queue_;
};
