#include "api/OverlayApi.h"
#include "host/OverlayHost.h"

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL StartOverlay(const LuvLetterOverlayStartOptions* options)
{
	if (options == nullptr || options->logoData == nullptr || options->logoSize <= 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().Start(*options);
}

extern "C" __declspec(dllexport) void LUVLETTER_OVERLAY_CALL StopOverlay()
{
	OverlayHost::Instance().Stop();
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayLayout(
	const LuvLetterOverlayLayoutConfig* layoutConfig)
{
	if (layoutConfig == nullptr)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateLayout(*layoutConfig);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayLogo(
	const uint8_t* logoData,
	int32_t logoSize)
{
	if (logoData == nullptr || logoSize <= 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateLogo(logoData, static_cast<size_t>(logoSize));
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayText(
	const wchar_t* text,
	int32_t textLength)
{
	if (text == nullptr && textLength > 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateText(text, textLength);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayInputText(
	const wchar_t* text,
	int32_t textLength)
{
	if (text == nullptr && textLength > 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateInputText(text, textLength);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayInputPromptText(
	const wchar_t* text,
	int32_t textLength)
{
	if (text == nullptr && textLength > 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateInputPromptText(text, textLength);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayInputSelection(
	int32_t selectionStart,
	int32_t selectionLength,
	int32_t caretIndex)
{
	return OverlayHost::Instance().UpdateInputSelection(selectionStart, selectionLength, caretIndex);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayOutputText(
	const wchar_t* text,
	int32_t textLength)
{
	if (text == nullptr && textLength > 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().UpdateOutputText(text, textLength);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL UpdateOverlayOutputNavigation(
	int32_t canPageUp,
	int32_t canPageDown)
{
	return OverlayHost::Instance().UpdateOutputNavigation(canPageUp != 0, canPageDown != 0);
}

extern "C" __declspec(dllexport) int LUVLETTER_OVERLAY_CALL SetOverlayVisualMode(int32_t visualMode)
{
	return OverlayHost::Instance().SetVisualMode(static_cast<LuvLetterOverlayVisualMode>(visualMode));
}

extern "C" __declspec(dllexport) void LUVLETTER_OVERLAY_CALL SetOverlayEventCallback(
	LuvLetterOverlayEventCallback callback,
	void* context)
{
	OverlayHost::Instance().SetEventCallback(callback, context);
}
