#pragma once

#include <cstdint>

#ifdef _WIN32
#define LUVLETTER_NATIVE_CALL __stdcall
#define LUVLETTER_NATIVE_EXPORT __declspec(dllexport)
#else
#define LUVLETTER_NATIVE_CALL
#define LUVLETTER_NATIVE_EXPORT
#endif

extern "C"
{
	struct LuvLetterInputBoxConfig
	{
		int32_t width;
		int32_t height;
		float cornerRadius;
		float borderThickness;
		float fontSize;
		float horizontalPadding;
		int32_t positionMode;
		int32_t offsetX;
		int32_t offsetY;
		int32_t bottomMargin;
		int32_t customX;
		int32_t customY;
		uint32_t borderColor;
		uint32_t backgroundColor;
		uint32_t textColor;
		uint32_t caretColor;
		int32_t submitVirtualKey;
		int32_t cancelVirtualKey;
		int32_t backspaceVirtualKey;
		int32_t submitModifiers;
		int32_t cancelModifiers;
		int32_t backspaceModifiers;
	};

	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ApplyInputBoxConfig(
		const LuvLetterInputBoxConfig* config);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ShowInputBox();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL HideInputBox();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ToggleInputBox();
	LUVLETTER_NATIVE_EXPORT void LUVLETTER_NATIVE_CALL ShutdownInputBox();
}
