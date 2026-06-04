#include "OverlayHost.h"

extern "C" __declspec(dllexport) int __stdcall StartOverlay(const BYTE* logoData, int logoSize)
{
	if (logoData == nullptr || logoSize <= 0)
	{
		return E_INVALIDARG;
	}

	return OverlayHost::Instance().Start(logoData, static_cast<size_t>(logoSize));
}

extern "C" __declspec(dllexport) void __stdcall StopOverlay()
{
	OverlayHost::Instance().Stop();
}
