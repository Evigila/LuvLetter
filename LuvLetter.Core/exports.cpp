#include "OverlayHost.h"

extern "C" __declspec(dllexport) int __stdcall StartOverlay()
{
	return OverlayHost::Instance().Start();
}

extern "C" __declspec(dllexport) void __stdcall StopOverlay()
{
	OverlayHost::Instance().Stop();
}
