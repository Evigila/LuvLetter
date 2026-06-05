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
	OverlayOutputTextUpdateRequest,
	OverlayOutputNavigationUpdateRequest,
	OverlayVisualModeUpdateRequest>;
