#include "configuration/NativeConfigurationSanitizer.h"
#include "rendering/SurfaceStyleDefaults.h"

#include <Windows.h>

#include <algorithm>
#include <cmath>

namespace
{
	float FiniteOr(float value, float fallback) noexcept
	{
		return std::isfinite(value) ? value : fallback;
	}
}

using namespace LuvLetterNative;

LuvLetterInputBoxConfig NativeConfigurationSanitizer::DefaultInputBox() noexcept
{
	LuvLetterInputBoxConfig config{};
	config.structSize = sizeof(config);
	config.abiVersion = LUVLETTER_NATIVE_ABI_VERSION;
	config.width = 560;
	config.height = 32;
	config.cornerRadius = SurfaceCornerRadius;
	config.borderThickness = SurfaceBorderThickness;
	config.fontSize = 14.0f;
	config.horizontalPadding = 10.0f;
	config.verticalPadding = 4.0f;
	config.caretWidth = 2.25f;
	config.positionMode = 0;
	config.bottomMargin = 60;
	config.borderColor = SurfaceBorderColor;
	config.backgroundColor = SurfaceBackgroundColor;
	config.textColor = SurfaceContentColor;
	config.caretColor = SurfaceContentColor;
	config.submitVirtualKey = VK_RETURN;
	config.cancelVirtualKey = VK_ESCAPE;
	config.backspaceVirtualKey = VK_BACK;
	return config;
}

LuvLetterFeatureWindowConfig NativeConfigurationSanitizer::DefaultQuickActionsWindow() noexcept
{
	LuvLetterFeatureWindowConfig config{};
	config.structSize = sizeof(config);
	config.abiVersion = LUVLETTER_NATIVE_ABI_VERSION;
	config.itemsPerPage = 9;
	config.cellSize = 96.0f;
	config.gap = 12.0f;
	config.cornerRadius = SurfaceCornerRadius;
	config.borderThickness = SurfaceBorderThickness;
	config.fontSize = 16.0f;
	config.bottomMargin = 60;
	config.borderColor = SurfaceBorderColor;
	config.backgroundColor = SurfaceBackgroundColor;
	config.textColor = SurfaceContentColor;
	config.accentColor = SurfaceContentColor;
	config.previousVirtualKey = VK_OEM_MINUS;
	config.nextVirtualKey = VK_OEM_PLUS;
	config.cancelVirtualKey = VK_ESCAPE;
	config.firstItemVirtualKey = L'1';
	return config;
}

LuvLetterInputBoxConfig NativeConfigurationSanitizer::SanitizeInputBox(
	const LuvLetterInputBoxConfig& config) noexcept
{
	auto sanitized = DefaultInputBox();
	sanitized.width = (std::clamp)(config.width, 120, 7680);
	sanitized.height = (std::clamp)(config.height, 24, 512);
	sanitized.cornerRadius = (std::clamp)(
		FiniteOr(config.cornerRadius, sanitized.cornerRadius),
		0.0f,
		512.0f);
	sanitized.borderThickness = (std::clamp)(
		FiniteOr(config.borderThickness, sanitized.borderThickness),
		0.0f,
		(std::min)(16.0f, static_cast<float>(sanitized.height) / 2.0f));
	sanitized.fontSize = (std::clamp)(
		FiniteOr(config.fontSize, sanitized.fontSize),
		6.0f,
		256.0f);
	sanitized.horizontalPadding = (std::clamp)(
		FiniteOr(config.horizontalPadding, sanitized.horizontalPadding),
		0.0f,
		static_cast<float>(sanitized.width) / 2.0f);
	sanitized.verticalPadding = (std::clamp)(
		FiniteOr(config.verticalPadding, sanitized.verticalPadding),
		0.0f,
		static_cast<float>(sanitized.height) / 2.0f);
	sanitized.caretWidth = (std::clamp)(
		FiniteOr(config.caretWidth, sanitized.caretWidth),
		0.5f,
		16.0f);
	sanitized.positionMode = config.positionMode >= 0 && config.positionMode <= 3
		? config.positionMode
		: sanitized.positionMode;
	sanitized.offsetX = (std::clamp)(config.offsetX, -32768, 32768);
	sanitized.offsetY = (std::clamp)(config.offsetY, -32768, 32768);
	sanitized.bottomMargin = (std::clamp)(config.bottomMargin, 0, 4096);
	sanitized.customX = (std::clamp)(config.customX, -32768, 32768);
	sanitized.customY = (std::clamp)(config.customY, -32768, 32768);
	sanitized.borderColor = config.borderColor;
	sanitized.backgroundColor = config.backgroundColor;
	sanitized.textColor = config.textColor;
	sanitized.caretColor = config.caretColor;
	sanitized.submitVirtualKey = config.submitVirtualKey > 0 && config.submitVirtualKey <= 0xFF
		? config.submitVirtualKey
		: sanitized.submitVirtualKey;
	sanitized.cancelVirtualKey = config.cancelVirtualKey > 0 && config.cancelVirtualKey <= 0xFF
		? config.cancelVirtualKey
		: sanitized.cancelVirtualKey;
	sanitized.backspaceVirtualKey = config.backspaceVirtualKey > 0
		&& config.backspaceVirtualKey <= 0xFF
		? config.backspaceVirtualKey
		: sanitized.backspaceVirtualKey;
	sanitized.submitModifiers = config.submitModifiers & 0xF;
	sanitized.cancelModifiers = config.cancelModifiers & 0xF;
	sanitized.backspaceModifiers = config.backspaceModifiers & 0xF;
	return sanitized;
}
LuvLetterFeatureWindowConfig NativeConfigurationSanitizer::SanitizeQuickActionsWindow(
	const LuvLetterFeatureWindowConfig& config) noexcept
{
	auto sanitized = DefaultQuickActionsWindow();
	sanitized.itemsPerPage = (std::clamp)(config.itemsPerPage, 1, 9);
	sanitized.cellSize = (std::clamp)(
		FiniteOr(config.cellSize, sanitized.cellSize),
		32.0f,
		512.0f);
	sanitized.gap = (std::clamp)(FiniteOr(config.gap, sanitized.gap), 0.0f, 128.0f);
	sanitized.cornerRadius = (std::clamp)(
		FiniteOr(config.cornerRadius, sanitized.cornerRadius),
		0.0f,
		256.0f);
	sanitized.borderThickness = (std::clamp)(
		FiniteOr(config.borderThickness, sanitized.borderThickness),
		0.0f,
		(std::min)(16.0f, sanitized.cellSize / 2.0f));
	sanitized.fontSize = (std::clamp)(
		FiniteOr(config.fontSize, sanitized.fontSize),
		6.0f,
		128.0f);
	sanitized.bottomMargin = (std::clamp)(config.bottomMargin, 0, 4096);
	sanitized.offsetX = (std::clamp)(config.offsetX, -32768, 32768);
	sanitized.offsetY = (std::clamp)(config.offsetY, -32768, 32768);
	sanitized.borderColor = config.borderColor;
	sanitized.backgroundColor = config.backgroundColor;
	sanitized.textColor = config.textColor;
	sanitized.accentColor = config.accentColor;
	sanitized.previousVirtualKey = config.previousVirtualKey > 0
		&& config.previousVirtualKey <= 0xFF
		? config.previousVirtualKey
		: sanitized.previousVirtualKey;
	sanitized.nextVirtualKey = config.nextVirtualKey > 0 && config.nextVirtualKey <= 0xFF
		? config.nextVirtualKey
		: sanitized.nextVirtualKey;
	sanitized.cancelVirtualKey = config.cancelVirtualKey > 0 && config.cancelVirtualKey <= 0xFF
		? config.cancelVirtualKey
		: sanitized.cancelVirtualKey;
	const auto maximumFirstItemKey = L'9' - sanitized.itemsPerPage + 1;
	sanitized.firstItemVirtualKey = config.firstItemVirtualKey >= L'0'
		&& config.firstItemVirtualKey <= maximumFirstItemKey
		? config.firstItemVirtualKey
		: sanitized.firstItemVirtualKey;
	sanitized.previousModifiers = config.previousModifiers & 0xF;
	sanitized.nextModifiers = config.nextModifiers & 0xF;
	sanitized.cancelModifiers = config.cancelModifiers & 0xF;
	return sanitized;
}
