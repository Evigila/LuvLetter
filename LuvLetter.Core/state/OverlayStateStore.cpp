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
		config.logoWidth = 28.0f;
		config.logoHeight = 28.0f;
		config.logoOffsetX = 0.0f;
		config.logoOffsetY = 0.0f;
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

void OverlayStateStore::UpdateInputText(std::wstring_view text)
{
	state_.inputText.assign(text.begin(), text.end());
}

void OverlayStateStore::UpdateOutputText(std::wstring_view text)
{
	state_.outputText.assign(text.begin(), text.end());
}

void OverlayStateStore::SetVisualMode(LuvLetterOverlayVisualMode visualMode)
{
	state_.visualMode = visualMode;
}

const OverlayState& OverlayStateStore::Snapshot() const
{
	return state_;
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
	sanitized.logoWidth = layoutConfig.logoWidth > 0.0f ? layoutConfig.logoWidth : sanitized.logoWidth;
	sanitized.logoHeight = layoutConfig.logoHeight > 0.0f ? layoutConfig.logoHeight : sanitized.logoHeight;
	sanitized.logoOffsetX = layoutConfig.logoOffsetX;
	sanitized.logoOffsetY = layoutConfig.logoOffsetY;
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
