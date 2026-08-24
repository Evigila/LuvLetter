#pragma once

#include "api/InputBoxApi.h"

// Defines Native defaults and constrains managed configuration values before
// they reach window geometry, input handling, or rendering code.
class NativeConfigurationSanitizer final
{
public:
	static LuvLetterInputBoxConfig DefaultInputBox() noexcept;
	static LuvLetterFeatureWindowConfig DefaultFeatureWindow() noexcept;
	static LuvLetterInputBoxConfig SanitizeInputBox(
		const LuvLetterInputBoxConfig& config) noexcept;
	static LuvLetterFeatureWindowConfig SanitizeFeatureWindow(
		const LuvLetterFeatureWindowConfig& config) noexcept;
};
