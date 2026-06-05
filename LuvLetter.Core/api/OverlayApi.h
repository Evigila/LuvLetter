#pragma once

#include <cstdint>

#ifdef _WIN32
#define LUVLETTER_OVERLAY_CALL __stdcall
#else
#define LUVLETTER_OVERLAY_CALL
#endif

extern "C"
{
	enum LuvLetterOverlayEventKind : int32_t
	{
		LuvLetterOverlayEventKind_None = 0,
		LuvLetterOverlayEventKind_InputChanged = 1,
		LuvLetterOverlayEventKind_CommandSubmitted = 2,
	};

	enum LuvLetterOverlayVisualMode : int32_t
	{
		LuvLetterOverlayVisualMode_Badge = 0,
		LuvLetterOverlayVisualMode_CommandLine = 1,
	};

	struct LuvLetterOverlayLayoutConfig
	{
		int32_t overlayWidth;
		int32_t overlayHeight;
		int32_t commandBarWidth;
		int32_t screenMarginLeft;
		int32_t screenMarginBottom;
		float contentPaddingLeft;
		float contentPaddingTop;
		float contentPaddingRight;
		float contentPaddingBottom;
		float logoWidth;
		float logoHeight;
		float logoOffsetX;
		float logoOffsetY;
		float courtesyZoneOffsetX;
		float courtesyZoneOffsetY;
		float courtesyZoneWidth;
		float courtesyZoneHeight;
		uint32_t badgeInactiveDelayMs;
		float badgeInactiveOpacity;
		float commandOutputHeight;
		float textReservedHeight;
		float elementGap;
		uint32_t animationDurationMs;
	};

	struct LuvLetterOverlayStartOptions
	{
		const uint8_t* logoData;
		int32_t logoSize;
		LuvLetterOverlayLayoutConfig layoutConfig;
		const wchar_t* initialInputText;
		int32_t initialInputTextLength;
	};

	struct LuvLetterOverlayEvent
	{
		int32_t kind;
		const wchar_t* text;
		int32_t textLength;
	};

	typedef void (LUVLETTER_OVERLAY_CALL* LuvLetterOverlayEventCallback)(
		const LuvLetterOverlayEvent* eventData,
		void* context);
}
