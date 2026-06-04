#pragma once

#include <Windows.h>
#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

class OverlayHost
{
public:
	static OverlayHost& Instance();

	HRESULT Start(const uint8_t* logoData, size_t logoSize);
	void Stop();

private:
	OverlayHost() = default;
	~OverlayHost() = default;
	OverlayHost(const OverlayHost&) = delete;
	OverlayHost& operator=(const OverlayHost&) = delete;

	HRESULT Run();
	HRESULT CreateDeviceIndependentResources();
	HRESULT CreateDeviceResources();
	HRESULT CreateLogoBitmap();
	void DiscardDeviceResources();
	HRESULT CreateOverlayWindow();
	void Render();
	void RecalculateWindowBounds();
	void MoveWindowTo(int x, int y) const;
	void AdvanceAnimation();
	LRESULT HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = S_OK;
	std::atomic<bool> running_ = false;

	HWND hwnd_ = nullptr;
	std::vector<uint8_t> logoBytes_;
	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IWICImagingFactory> wicFactory_;
	Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<ID2D1Bitmap> logoBitmap_;
	int startX_ = 0;
	int startY_ = 0;
	int targetX_ = 0;
	int targetY_ = 0;
	ULONGLONG animationStartTick_ = 0;
	bool animationCompleted_ = false;
};
