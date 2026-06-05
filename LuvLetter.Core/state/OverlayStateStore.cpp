#include "state/OverlayStateStore.h"

#include <algorithm>

namespace
{
	LuvLetterOverlayLayoutConfig CreateDefaultLayoutConfig()
	{
		LuvLetterOverlayLayoutConfig config{};
		config.overlayWidth = 50;
		config.overlayHeight = 50;
		config.commandBarWidth = 420;
		config.screenMarginLeft = 20;
		config.screenMarginBottom = 20;
		config.contentPaddingLeft = 8.0f;
		config.contentPaddingTop = 8.0f;
		config.contentPaddingRight = 8.0f;
		config.contentPaddingBottom = 8.0f;
		config.logoWidth = 0.0f;
		config.logoHeight = 0.0f;
		config.logoOffsetX = 0.0f;
		config.logoOffsetY = 0.0f;
		config.courtesyZoneOffsetX = 0.0f;
		config.courtesyZoneOffsetY = 0.0f;
		config.courtesyZoneWidth = 0.0f;
		config.courtesyZoneHeight = 0.0f;
		config.badgeInactiveDelayMs = 5000;
		config.badgeInactiveOpacity = 0.5f;
		config.commandOutputHeight = 120.0f;
		config.textReservedHeight = 0.0f;
		config.elementGap = 8.0f;
		config.animationDurationMs = 180;
		return config;
	}
}

void OverlayStateStore::Initialize(const LuvLetterOverlayStartOptions& options)
{
	state_.logoBytes.assign(options.logoData, options.logoData + options.logoSize);
	state_.inputText = CopyText(options.initialInputText, options.initialInputTextLength);
	state_.outputText.clear();
	state_.visualMode = LuvLetterOverlayVisualMode_Badge;
	state_.layoutConfig = SanitizeLayoutConfig(options.layoutConfig);
	state_.badgeIsActive = true;
	state_.inputCursorVisible = false;
	state_.outputCanPageUp = false;
	state_.outputCanPageDown = false;
}

void OverlayStateStore::Reset()
{
	state_ = {};
}

void OverlayStateStore::UpdateLayout(const LuvLetterOverlayLayoutConfig& layoutConfig)
{
	state_.layoutConfig = SanitizeLayoutConfig(layoutConfig);
}

void OverlayStateStore::UpdateLogo(const uint8_t* logoData, size_t logoSize)
{
	state_.logoBytes.assign(logoData, logoData + logoSize);
}

void OverlayStateStore::UpdateInputPromptText(std::wstring_view text)
{
	state_.inputPromptText.assign(text.begin(), text.end());
	if (state_.inputPromptText.empty())
	{
		state_.inputPromptText = L"EN";
	}
}

void OverlayStateStore::UpdateInputText(std::wstring_view text)
{
	state_.inputText.assign(text.begin(), text.end());
	ClampInputSelection();
}

void OverlayStateStore::UpdateOutputText(std::wstring_view text)
{
	state_.outputText.assign(text.begin(), text.end());
}

void OverlayStateStore::SetVisualMode(LuvLetterOverlayVisualMode visualMode)
{
	state_.visualMode = visualMode;
}

void OverlayStateStore::SetBadgeActive(bool isActive)
{
	state_.badgeIsActive = isActive;
}

void OverlayStateStore::SetInputCursorVisible(bool isVisible)
{
	state_.inputCursorVisible = isVisible;
}

void OverlayStateStore::SetInputSelection(int32_t selectionStart, int32_t selectionLength, int32_t caretIndex)
{
	state_.inputSelectionStart = selectionStart;
	state_.inputSelectionLength = selectionLength;
	state_.inputCaretIndex = caretIndex;
	ClampInputSelection();
}

void OverlayStateStore::SetOutputNavigation(bool canPageUp, bool canPageDown)
{
	state_.outputCanPageUp = canPageUp;
	state_.outputCanPageDown = canPageDown;
}

const OverlayState& OverlayStateStore::Snapshot() const
{
	return state_;
}

void OverlayStateStore::ClampInputSelection()
{
	const auto textLength = static_cast<int32_t>(state_.inputText.size());
	state_.inputSelectionStart = (std::clamp)(state_.inputSelectionStart, 0, textLength);
	state_.inputSelectionLength = (std::clamp)(state_.inputSelectionLength, 0, textLength - state_.inputSelectionStart);
	state_.inputCaretIndex = (std::clamp)(state_.inputCaretIndex, 0, textLength);
}

LuvLetterOverlayLayoutConfig OverlayStateStore::SanitizeLayoutConfig(const LuvLetterOverlayLayoutConfig& layoutConfig)
{
	auto sanitized = CreateDefaultLayoutConfig();

	sanitized.overlayWidth = layoutConfig.overlayWidth > 0 ? layoutConfig.overlayWidth : sanitized.overlayWidth;
	sanitized.overlayHeight = layoutConfig.overlayHeight > 0 ? layoutConfig.overlayHeight : sanitized.overlayHeight;
	sanitized.commandBarWidth =
		layoutConfig.commandBarWidth > 0 ? layoutConfig.commandBarWidth : sanitized.commandBarWidth;
	sanitized.screenMarginLeft = layoutConfig.screenMarginLeft >= 0 ? layoutConfig.screenMarginLeft : sanitized.screenMarginLeft;
	sanitized.screenMarginBottom =
		layoutConfig.screenMarginBottom >= 0 ? layoutConfig.screenMarginBottom : sanitized.screenMarginBottom;
	sanitized.contentPaddingLeft =
		layoutConfig.contentPaddingLeft >= 0.0f ? layoutConfig.contentPaddingLeft : sanitized.contentPaddingLeft;
	sanitized.contentPaddingTop =
		layoutConfig.contentPaddingTop >= 0.0f ? layoutConfig.contentPaddingTop : sanitized.contentPaddingTop;
	sanitized.contentPaddingRight =
		layoutConfig.contentPaddingRight >= 0.0f ? layoutConfig.contentPaddingRight : sanitized.contentPaddingRight;
	sanitized.contentPaddingBottom =
		layoutConfig.contentPaddingBottom >= 0.0f ? layoutConfig.contentPaddingBottom : sanitized.contentPaddingBottom;
	sanitized.logoWidth =
		layoutConfig.logoWidth > 0.0f ? layoutConfig.logoWidth : static_cast<float>(sanitized.overlayWidth);
	sanitized.logoHeight =
		layoutConfig.logoHeight > 0.0f ? layoutConfig.logoHeight : static_cast<float>(sanitized.overlayHeight);
	sanitized.logoOffsetX = layoutConfig.logoOffsetX;
	sanitized.logoOffsetY = layoutConfig.logoOffsetY;
	sanitized.courtesyZoneOffsetX = layoutConfig.courtesyZoneOffsetX;
	sanitized.courtesyZoneOffsetY = layoutConfig.courtesyZoneOffsetY;
	sanitized.courtesyZoneWidth =
		layoutConfig.courtesyZoneWidth > 0.0f
			? layoutConfig.courtesyZoneWidth
			: static_cast<float>(sanitized.overlayWidth);
	sanitized.courtesyZoneHeight =
		layoutConfig.courtesyZoneHeight > 0.0f
			? layoutConfig.courtesyZoneHeight
			: static_cast<float>(sanitized.overlayHeight);
	sanitized.badgeInactiveDelayMs =
		layoutConfig.badgeInactiveDelayMs > 0 ? layoutConfig.badgeInactiveDelayMs : sanitized.badgeInactiveDelayMs;
	sanitized.badgeInactiveOpacity = layoutConfig.badgeInactiveOpacity >= 0.0f
		? (std::min)(1.0f, layoutConfig.badgeInactiveOpacity)
		: sanitized.badgeInactiveOpacity;
	sanitized.commandOutputHeight =
		std::max(0.0f, layoutConfig.commandOutputHeight);
	sanitized.textReservedHeight = std::max(0.0f, layoutConfig.textReservedHeight);
	sanitized.elementGap = layoutConfig.elementGap >= 0.0f ? layoutConfig.elementGap : sanitized.elementGap;
	sanitized.animationDurationMs =
		layoutConfig.animationDurationMs > 0 ? layoutConfig.animationDurationMs : sanitized.animationDurationMs;

	return sanitized;
}

std::wstring OverlayStateStore::CopyText(const wchar_t* text, int32_t textLength)
{
	if (text == nullptr || textLength <= 0)
	{
		return {};
	}

	return std::wstring(text, text + textLength);
}
