#pragma once

#include <Windows.h>
#include <d2d1.h>

struct OverlayLayoutSnapshot
{
	RECT badgeHiddenWindowRect{};
	RECT badgeVisibleWindowRect{};
	RECT badgeCourtesyZoneRect{};
	RECT commandBarWindowRect{};
	RECT commandVisibleWindowRect{};
	D2D1_RECT_F backgroundRect{};
	D2D1_RECT_F inputBarRect{};
	D2D1_RECT_F logoRect{};
	D2D1_RECT_F inputPromptRect{};
	D2D1_RECT_F inputTextRect{};
	D2D1_RECT_F outputPanelRect{};
	D2D1_RECT_F outputTextRect{};
	D2D1_RECT_F outputScrollUpRect{};
	D2D1_RECT_F outputScrollDownRect{};
	bool hasOutputArea = false;
};
