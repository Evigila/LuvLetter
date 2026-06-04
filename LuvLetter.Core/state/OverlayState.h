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
};
