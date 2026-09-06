#pragma once

#include "api/InputBoxApi.h"

#include <cstdint>

namespace ArkheideSystem
{
	inline constexpr int CandidateAltModifier = 1;
	inline constexpr int CandidateControlModifier = 2;
	inline constexpr int CandidateShiftModifier = 4;
	inline constexpr int CandidateWindowsModifier = 8;

	inline bool TryResolveEnterAction(
		int modifiers,
		LuvLetterCandidateAction& action) noexcept
	{
		if ((modifiers & (CandidateAltModifier | CandidateWindowsModifier)) != 0
			|| (modifiers & ~(CandidateControlModifier | CandidateShiftModifier)) != 0)
		{
			return false;
		}

		action = (modifiers & CandidateControlModifier) != 0
			? LuvLetterCandidateActionCopyPath
			: (modifiers & CandidateShiftModifier) != 0
				? LuvLetterCandidateActionReveal
				: LuvLetterCandidateActionOpen;
		return true;
	}

	inline const wchar_t* CandidateActionLabel(
		int32_t actions,
		int modifiers) noexcept
	{
		if ((actions & LuvLetterCandidateActionsCopyPath) == 0)
		{
			return nullptr;
		}
		if ((modifiers & CandidateControlModifier) != 0)
		{
			return L"\u590d\u5236\u8def\u5f84";
		}
		if ((modifiers & CandidateShiftModifier) != 0)
		{
			return L"\u6253\u5f00\u5230\u6587\u4ef6\u5939";
		}
		return L"\u6253\u5f00";
	}
}
