#include "interop/OverlayEventBridge.h"

void OverlayEventBridge::SetCallback(LuvLetterOverlayEventCallback callback, void* context)
{
	std::lock_guard lock(mutex_);
	callback_ = callback;
	context_ = context;
}

void OverlayEventBridge::Publish(int32_t eventKind, std::wstring_view text) const
{
	LuvLetterOverlayEventCallback callback = nullptr;
	void* context = nullptr;
	{
		std::lock_guard lock(mutex_);
		callback = callback_;
		context = context_;
	}

	if (callback == nullptr)
	{
		return;
	}

	LuvLetterOverlayEvent eventData{};
	eventData.kind = eventKind;
	eventData.text = text.empty() ? nullptr : text.data();
	eventData.textLength = static_cast<int32_t>(text.size());
	callback(&eventData, context);
}
