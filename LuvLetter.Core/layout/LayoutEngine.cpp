#include "layout/LayoutEngine.h"

#include <algorithm>
#include <cmath>

namespace
{
	float ClampNonNegative(float value)
	{
		return (std::max)(0.0f, value);
	}

	D2D1_RECT_F CreateRect(float left, float top, float right, float bottom)
	{
		return D2D1::RectF(left, top, (std::max)(left, right), (std::max)(top, bottom));
	}
}

OverlayLayoutSnapshot LayoutEngine::Compute(
	const OverlayState& state,
	const MONITORINFO& monitorInfo,
	const RECT& currentWindowRect) const
{
	const auto& config = state.layoutConfig;

	OverlayLayoutSnapshot snapshot{};

	const auto badgeWidth = (std::max)(1, config.overlayWidth);
	const auto badgeHeight = (std::max)(1, config.overlayHeight);
	const auto commandWidth = (std::max)(badgeWidth, config.commandBarWidth);
	const auto commandOutputHeight = ClampNonNegative(config.commandOutputHeight);
	const auto commandGap = commandOutputHeight > 0.0f ? ClampNonNegative(config.elementGap) : 0.0f;

	const auto visibleLeft = monitorInfo.rcWork.left + config.screenMarginLeft;
	const auto badgeTop = monitorInfo.rcWork.bottom - config.screenMarginBottom - badgeHeight;
	snapshot.badgeVisibleWindowRect = {
		visibleLeft,
		badgeTop,
		visibleLeft + badgeWidth,
		badgeTop + badgeHeight,
	};

	snapshot.badgeHiddenWindowRect = {
		monitorInfo.rcMonitor.left - badgeWidth,
		badgeTop,
		monitorInfo.rcMonitor.left,
		badgeTop + badgeHeight,
	};

	snapshot.commandBarWindowRect = {
		visibleLeft,
		badgeTop,
		visibleLeft + commandWidth,
		badgeTop + badgeHeight,
	};

	snapshot.commandVisibleWindowRect = {
		visibleLeft,
		badgeTop - static_cast<LONG>(std::lround(commandOutputHeight + commandGap)),
		visibleLeft + commandWidth,
		badgeTop + badgeHeight,
	};

	const auto currentWidth = static_cast<float>((std::max)(1L, currentWindowRect.right - currentWindowRect.left));
	const auto currentHeight = static_cast<float>((std::max)(1L, currentWindowRect.bottom - currentWindowRect.top));
	snapshot.backgroundRect = D2D1::RectF(0.0f, 0.0f, currentWidth, currentHeight);

	const auto contentPaddingLeft = ClampNonNegative(config.contentPaddingLeft);
	const auto contentPaddingTop = ClampNonNegative(config.contentPaddingTop);
	const auto contentPaddingRight = ClampNonNegative(config.contentPaddingRight);
	const auto contentPaddingBottom = ClampNonNegative(config.contentPaddingBottom);

	if (state.visualMode == LuvLetterOverlayVisualMode_Badge)
	{
		const auto contentRight = (std::max)(contentPaddingLeft, currentWidth - contentPaddingRight);
		const auto contentBottom = (std::max)(contentPaddingTop, currentHeight - contentPaddingBottom);
		const auto availableWidth = (std::max)(0.0f, contentRight - contentPaddingLeft);
		const auto availableHeight = (std::max)(0.0f, contentBottom - contentPaddingTop);
		const auto logoHalfWidth = (std::min)(config.logoWidth * 0.5f, availableWidth * 0.5f);
		const auto logoHalfHeight = (std::min)(config.logoHeight * 0.5f, availableHeight * 0.5f);
		const auto logoCenterX = (currentWidth * 0.5f) + config.logoOffsetX;
		const auto logoCenterY = (currentHeight * 0.5f) + config.logoOffsetY;
		snapshot.logoRect = CreateRect(
			logoCenterX - logoHalfWidth,
			logoCenterY - logoHalfHeight,
			logoCenterX + logoHalfWidth,
			logoCenterY + logoHalfHeight);
		return snapshot;
	}

	const auto inputBarHeight = static_cast<float>(badgeHeight);
	const auto inputBarTop = (std::max)(0.0f, currentHeight - inputBarHeight);
	snapshot.inputBarRect = CreateRect(0.0f, inputBarTop, currentWidth, currentHeight);

	const auto badgePanelWidth = (std::min)(currentWidth, static_cast<float>(badgeWidth));
	const auto badgeContentLeft = contentPaddingLeft;
	const auto badgeContentTop = inputBarTop + contentPaddingTop;
	const auto badgeContentRight = (std::max)(badgeContentLeft, badgePanelWidth - contentPaddingRight);
	const auto badgeContentBottom = (std::max)(badgeContentTop, currentHeight - contentPaddingBottom);
	const auto badgeAvailableWidth = (std::max)(0.0f, badgeContentRight - badgeContentLeft);
	const auto badgeAvailableHeight = (std::max)(0.0f, badgeContentBottom - badgeContentTop);
	const auto logoHalfWidth = (std::min)(config.logoWidth * 0.5f, badgeAvailableWidth * 0.5f);
	const auto logoHalfHeight = (std::min)(config.logoHeight * 0.5f, badgeAvailableHeight * 0.5f);
	const auto logoCenterX = (badgePanelWidth * 0.5f) + config.logoOffsetX;
	const auto logoCenterY = ((inputBarTop + currentHeight) * 0.5f) + config.logoOffsetY;
	snapshot.logoRect = CreateRect(
		logoCenterX - logoHalfWidth,
		logoCenterY - logoHalfHeight,
		logoCenterX + logoHalfWidth,
		logoCenterY + logoHalfHeight);

	const auto promptLeft = (std::min)(currentWidth, badgePanelWidth + commandGap);
	const auto promptRight = (std::min)(currentWidth, promptLeft + 18.0f);
	snapshot.inputPromptRect = CreateRect(promptLeft, inputBarTop, promptRight, currentHeight);
	snapshot.inputTextRect = CreateRect(
		(std::min)(currentWidth, promptRight + 6.0f),
		inputBarTop,
		(std::max)(promptRight + 6.0f, currentWidth - contentPaddingRight),
		currentHeight);

	const auto outputBottom = (std::max)(0.0f, inputBarTop - commandGap);
	if (outputBottom > 0.0f)
	{
		snapshot.outputPanelRect = CreateRect(0.0f, 0.0f, currentWidth, outputBottom);
		snapshot.outputTextRect = CreateRect(
			contentPaddingLeft,
			contentPaddingTop,
			(std::max)(contentPaddingLeft, currentWidth - contentPaddingRight),
			(std::max)(contentPaddingTop, outputBottom - contentPaddingBottom));
		snapshot.hasOutputArea = true;
	}

	return snapshot;
}
