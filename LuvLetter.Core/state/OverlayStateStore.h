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
	void UpdateInputText(std::wstring_view text);
	void UpdateOutputText(std::wstring_view text);
	void SetVisualMode(LuvLetterOverlayVisualMode visualMode);

	const OverlayState& Snapshot() const;

private:
	static LuvLetterOverlayLayoutConfig SanitizeLayoutConfig(const LuvLetterOverlayLayoutConfig& layoutConfig);
	static std::wstring CopyText(const wchar_t* text, int32_t textLength);

	OverlayState state_{};
};
