#pragma once

#include "api/OverlayApi.h"

#include <string>
#include <variant>
#include <vector>

struct OverlayLayoutUpdateRequest
{
	LuvLetterOverlayLayoutConfig layoutConfig{};
};

struct OverlayLogoUpdateRequest
{
	std::vector<uint8_t> logoBytes;
};

struct OverlayTextUpdateRequest
{
	std::wstring text;
};

struct OverlayInputTextUpdateRequest
{
	std::wstring text;
};

struct OverlayInputPromptTextUpdateRequest
{
	std::wstring text;
};

struct OverlayInputSelectionUpdateRequest
{
	int32_t selectionStart = 0;
	int32_t selectionLength = 0;
	int32_t caretIndex = 0;
};

struct OverlayOutputTextUpdateRequest
{
	std::wstring text;
};

struct OverlayOutputNavigationUpdateRequest
{
	bool canPageUp = false;
	bool canPageDown = false;
};

struct OverlayVisualModeUpdateRequest
{
	LuvLetterOverlayVisualMode visualMode = LuvLetterOverlayVisualMode_Badge;
};

using OverlayRequest = std::variant<
	OverlayLayoutUpdateRequest,
	OverlayLogoUpdateRequest,
	OverlayTextUpdateRequest,
	OverlayInputTextUpdateRequest,
	OverlayInputPromptTextUpdateRequest,
	OverlayInputSelectionUpdateRequest,
	OverlayOutputTextUpdateRequest,
	OverlayOutputNavigationUpdateRequest,
	OverlayVisualModeUpdateRequest>;
