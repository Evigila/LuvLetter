#pragma once

#include "layout/LayoutSnapshot.h"
#include "state/OverlayState.h"

class LayoutEngine
{
public:
	OverlayLayoutSnapshot Compute(
		const OverlayState& state,
		const MONITORINFO& monitorInfo,
		const RECT& currentWindowRect) const;
};
