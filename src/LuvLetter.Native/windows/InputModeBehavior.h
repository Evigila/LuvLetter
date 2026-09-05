#pragma once

#include "api/InputBoxApi.h"

#include <string_view>

namespace ArkheideSystem
{
	inline constexpr LuvLetterInputMode ResolveInputModeForText(
		LuvLetterInputMode currentMode,
		std::wstring_view text) noexcept
	{
		return currentMode == LuvLetterInputModeGeneral
			&& !text.empty()
			&& text.front() == L'/'
			? LuvLetterInputModeCommand
			: currentMode;
	}
}
