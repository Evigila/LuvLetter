#pragma once

#include "api/OverlayApi.h"
#include "state/OverlayState.h"

#include <cstddef>
#include <string_view>

class OverlayStateStore
{
public:
	void Initialize(const LuvLetterOverlayStartOptions& options);
	void Reset();

	void UpdateLayout(const LuvLetterOverlayLayoutConfig& layoutConfig);
	void UpdateLogo(const uint8_t* logoData, size_t logoSize);
	void UpdateInputPromptText(std::wstring_view text);
	void UpdateInputText(std::wstring_view text);
	void UpdateOutputText(std::wstring_view text);
	void SetVisualMode(LuvLetterOverlayVisualMode visualMode);
	void SetBadgeActive(bool isActive);
	void SetInputCursorVisible(bool isVisible);
	void SetInputSelection(int32_t selectionStart, int32_t selectionLength, int32_t caretIndex);
	void SetOutputNavigation(bool canPageUp, bool canPageDown);

	const OverlayState& Snapshot() const;

private:
	void ClampInputSelection();
	static LuvLetterOverlayLayoutConfig SanitizeLayoutConfig(const LuvLetterOverlayLayoutConfig& layoutConfig);
	static std::wstring CopyText(const wchar_t* text, int32_t textLength);

	OverlayState state_{};
};
