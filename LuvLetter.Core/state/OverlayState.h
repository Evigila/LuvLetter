#pragma once

#include "api/OverlayApi.h"

#include <string>
#include <vector>

struct OverlayState
{
	std::vector<uint8_t> logoBytes;
	std::wstring inputText;
	std::wstring outputText;
	LuvLetterOverlayVisualMode visualMode = LuvLetterOverlayVisualMode_Badge;
	LuvLetterOverlayLayoutConfig layoutConfig{};
	bool badgeIsActive = true;
	bool inputCursorVisible = false;
	bool outputCanPageUp = false;
	bool outputCanPageDown = false;
};
