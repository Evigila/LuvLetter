#pragma once

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <atomic>

class OverlayHost
{
public:
	static OverlayHost& Instance();

	HRESULT Start();
	void Stop();

private:
	OverlayHost() = default;
	~OverlayHost() = default;
	OverlayHost(const OverlayHost&) = delete;
	OverlayHost& operator=(const OverlayHost&) = delete;

	HRESULT Run();
	HRESULT CreateDeviceIndependentResources();
	HRESULT CreateDeviceResources();
	void DiscardDeviceResources();
	HRESULT CreateOverlayWindow();
	void Render();
	void UpdateWindowPosition() const;
	LRESULT HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = S_OK;
	std::atomic<bool> running_ = false;

	HWND hwnd_ = nullptr;
	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
};
