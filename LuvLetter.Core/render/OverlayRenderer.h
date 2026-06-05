#pragma once

#include "layout/LayoutSnapshot.h"
#include "state/OverlayState.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <vector>

class OverlayRenderer
{
public:
	HRESULT Initialize();
	HRESULT EnsureResources(HWND hwnd, const OverlayState& state);
	void Resize(UINT width, UINT height);
	void ResetLogoBitmap();
	void DiscardDeviceResources();
	void Render(HWND hwnd, const OverlayState& state, const OverlayLayoutSnapshot& layoutSnapshot);

private:
	HRESULT CreateLogoBitmap(const std::vector<uint8_t>& logoBytes);

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IWICImagingFactory> wicFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> inputTextFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> outputTextFormat_;
	Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<ID2D1Bitmap> logoBitmap_;
	Microsoft::WRL::ComPtr<ID2D1LinearGradientBrush> inputBarGradientBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> outputPanelBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> separatorBrush_;
	std::vector<uint8_t> cachedLogoBytes_;
};
