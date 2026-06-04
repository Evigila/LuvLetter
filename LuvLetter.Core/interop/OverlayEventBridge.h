#pragma once

#include "api/OverlayApi.h"

#include <mutex>
#include <string_view>

class OverlayEventBridge
{
public:
	void SetCallback(LuvLetterOverlayEventCallback callback, void* context);
	void Publish(int32_t eventKind, std::wstring_view text) const;

private:
	mutable std::mutex mutex_;
	LuvLetterOverlayEventCallback callback_ = nullptr;
	void* context_ = nullptr;
};
