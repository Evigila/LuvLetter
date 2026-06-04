#include "OverlayHost.h"

#include <cmath>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "windowscodecs.lib")

namespace
{
	constexpr wchar_t WindowClassName[] = L"LuvLetter.Core.OverlayWindow";
	constexpr int OverlayWidth = 50;
	constexpr int OverlayHeight = 50;
	constexpr int OverlayMargin = 20;
	constexpr float LogoDrawSize = 28.0f;
	constexpr UINT_PTR AnimationTimerId = 1;
	constexpr DWORD AnimationDurationMs = 180;
}

OverlayHost& OverlayHost::Instance()
{
	static OverlayHost instance;
	return instance;
}

HRESULT OverlayHost::Start(const uint8_t* logoData, size_t logoSize)
{
	if (running_)
	{
		return S_FALSE;
	}

	if (threadHandle_ != nullptr)
	{
		return S_FALSE;
	}

	if (logoData == nullptr || logoSize == 0)
	{
		return E_INVALIDARG;
	}

	logoBytes_.assign(logoData, logoData + logoSize);
	logoBitmap_.Reset();
	animationCompleted_ = false;

	startedEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (startedEvent_ == nullptr)
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	startResult_ = E_FAIL;
	threadHandle_ = CreateThread(nullptr, 0, &OverlayHost::ThreadEntry, this, 0, &threadId_);
	if (threadHandle_ == nullptr)
	{
		const auto hr = HRESULT_FROM_WIN32(GetLastError());
		CloseHandle(startedEvent_);
		startedEvent_ = nullptr;
		return hr;
	}

	WaitForSingleObject(startedEvent_, INFINITE);
	CloseHandle(startedEvent_);
	startedEvent_ = nullptr;

	if (FAILED(startResult_))
	{
		WaitForSingleObject(threadHandle_, INFINITE);
		CloseHandle(threadHandle_);
		threadHandle_ = nullptr;
		threadId_ = 0;
	}

	return startResult_;
}

void OverlayHost::Stop()
{
	if (threadHandle_ == nullptr)
	{
		return;
	}

	if (threadId_ != 0)
	{
		PostThreadMessageW(threadId_, WM_QUIT, 0, 0);
	}

	WaitForSingleObject(threadHandle_, INFINITE);
	CloseHandle(threadHandle_);
	threadHandle_ = nullptr;
	threadId_ = 0;
}

DWORD WINAPI OverlayHost::ThreadEntry(LPVOID parameter)
{
	auto* host = static_cast<OverlayHost*>(parameter);
	host->startResult_ = host->Run();
	if (host->startedEvent_ != nullptr)
	{
		SetEvent(host->startedEvent_);
	}

	return static_cast<DWORD>(FAILED(host->startResult_) ? host->startResult_ : 0);
}

HRESULT OverlayHost::Run()
{
	const auto comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	const auto needsUninitialize = SUCCEEDED(comResult);

	const auto createWindowResult = CreateOverlayWindow();
	if (FAILED(createWindowResult))
	{
		if (needsUninitialize)
		{
			CoUninitialize();
		}

		return createWindowResult;
	}

	running_ = true;
	if (startedEvent_ != nullptr)
	{
		SetEvent(startedEvent_);
	}

	MSG message{};
	while (GetMessageW(&message, nullptr, 0, 0) > 0)
	{
		TranslateMessage(&message);
		DispatchMessageW(&message);
	}

	if (hwnd_ != nullptr)
	{
		DestroyWindow(hwnd_);
		hwnd_ = nullptr;
	}

	DiscardDeviceResources();
	wicFactory_.Reset();
	d2dFactory_.Reset();
	running_ = false;

	if (needsUninitialize)
	{
		CoUninitialize();
	}

	return S_OK;
}

HRESULT OverlayHost::CreateDeviceIndependentResources()
{
	if (!d2dFactory_)
	{
		auto hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!wicFactory_)
	{
		auto hr = CoCreateInstance(
			CLSID_WICImagingFactory,
			nullptr,
			CLSCTX_INPROC_SERVER,
			IID_PPV_ARGS(wicFactory_.GetAddressOf()));
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

HRESULT OverlayHost::CreateLogoBitmap()
{
	if (logoBitmap_)
	{
		return S_OK;
	}

	if (logoBytes_.empty())
	{
		return E_INVALIDARG;
	}

	if (!renderTarget_)
	{
		return E_UNEXPECTED;
	}

	auto hr = CreateDeviceIndependentResources();
	if (FAILED(hr))
	{
		return hr;
	}

	Microsoft::WRL::ComPtr<IWICStream> stream;
	hr = wicFactory_->CreateStream(stream.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	hr = stream->InitializeFromMemory(
		const_cast<BYTE*>(logoBytes_.data()),
		static_cast<DWORD>(logoBytes_.size()));
	if (FAILED(hr))
	{
		return hr;
	}

	Microsoft::WRL::ComPtr<IWICBitmapDecoder> decoder;
	hr = wicFactory_->CreateDecoderFromStream(
		stream.Get(),
		nullptr,
		WICDecodeMetadataCacheOnLoad,
		decoder.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	UINT frameCount = 0;
	hr = decoder->GetFrameCount(&frameCount);
	if (FAILED(hr) || frameCount == 0)
	{
		return FAILED(hr) ? hr : E_FAIL;
	}

	Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> selectedFrame;
	UINT selectedArea = 0;
	for (UINT index = 0; index < frameCount; ++index)
	{
		Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> frame;
		hr = decoder->GetFrame(index, frame.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		UINT width = 0;
		UINT height = 0;
		hr = frame->GetSize(&width, &height);
		if (FAILED(hr))
		{
			return hr;
		}

		const auto area = width * height;
		if (!selectedFrame || area > selectedArea)
		{
			selectedArea = area;
			selectedFrame = frame;
		}
	}

	if (!selectedFrame)
	{
		return E_FAIL;
	}

	Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
	hr = wicFactory_->CreateFormatConverter(converter.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	hr = converter->Initialize(
		selectedFrame.Get(),
		GUID_WICPixelFormat32bppPBGRA,
		WICBitmapDitherTypeNone,
		nullptr,
		0.0,
		WICBitmapPaletteTypeCustom);
	if (FAILED(hr))
	{
		return hr;
	}

	hr = renderTarget_->CreateBitmapFromWicBitmap(converter.Get(), logoBitmap_.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	return S_OK;
}

HRESULT OverlayHost::CreateDeviceResources()
{
	auto hr = CreateDeviceIndependentResources();
	if (FAILED(hr))
	{
		return hr;
	}

	if (!renderTarget_)
	{
		RECT rect{};
		GetClientRect(hwnd_, &rect);

		hr = d2dFactory_->CreateHwndRenderTarget(
			D2D1::RenderTargetProperties(),
			D2D1::HwndRenderTargetProperties(
				hwnd_,
				D2D1::SizeU(rect.right - rect.left, rect.bottom - rect.top),
				D2D1_PRESENT_OPTIONS_IMMEDIATELY),
			renderTarget_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!logoBitmap_)
	{
		hr = CreateLogoBitmap();
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

void OverlayHost::DiscardDeviceResources()
{
	logoBitmap_.Reset();
	renderTarget_.Reset();
}

void OverlayHost::RecalculateWindowBounds()
{
	POINT anchorPoint{ 0, GetSystemMetrics(SM_CYSCREEN) - 1 };
	const auto monitor = MonitorFromPoint(anchorPoint, MONITOR_DEFAULTTOPRIMARY);

	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (!GetMonitorInfoW(monitor, &monitorInfo))
	{
		startX_ = -OverlayWidth;
		startY_ = -OverlayHeight;
		targetX_ = OverlayMargin;
		targetY_ = OverlayMargin;
		return;
	}

	startX_ = monitorInfo.rcMonitor.left - OverlayWidth;
	startY_ = monitorInfo.rcWork.bottom - OverlayMargin - OverlayHeight;
	targetX_ = monitorInfo.rcWork.left + OverlayMargin;
	targetY_ = startY_;
}

void OverlayHost::MoveWindowTo(int x, int y) const
{
	if (hwnd_ == nullptr)
	{
		return;
	}

	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		x,
		y,
		OverlayWidth,
		OverlayHeight,
		SWP_NOACTIVATE);
}

void OverlayHost::AdvanceAnimation()
{
	if (animationCompleted_ || hwnd_ == nullptr)
	{
		return;
	}

	const auto elapsed = GetTickCount64() - animationStartTick_;
	const auto clampedElapsed = elapsed > AnimationDurationMs ? AnimationDurationMs : elapsed;
	const double progress = static_cast<double>(clampedElapsed) / static_cast<double>(AnimationDurationMs);
	const double easedProgress = progress * (2.0 - progress);
	const auto x = static_cast<int>(std::lround(
		static_cast<double>(startX_) + (static_cast<double>(targetX_ - startX_) * easedProgress)));

	MoveWindowTo(x, targetY_);

	if (elapsed >= AnimationDurationMs)
	{
		animationCompleted_ = true;
		KillTimer(hwnd_, AnimationTimerId);
		MoveWindowTo(targetX_, targetY_);
	}
}

HRESULT OverlayHost::CreateOverlayWindow()
{
	auto hr = CreateDeviceIndependentResources();
	if (FAILED(hr))
	{
		return hr;
	}

	RecalculateWindowBounds();

	const auto cleanupAndReturn = [&](HRESULT error) {
		if (hwnd_ != nullptr)
		{
			DestroyWindow(hwnd_);
			hwnd_ = nullptr;
		}

		return error;
	};

	WNDCLASSEXW windowClass{};
	windowClass.cbSize = sizeof(windowClass);
	windowClass.lpfnWndProc = &OverlayHost::WindowProc;
	windowClass.hInstance = GetModuleHandleW(nullptr);
	windowClass.lpszClassName = WindowClassName;
	windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);

	if (RegisterClassExW(&windowClass) == 0)
	{
		const auto lastError = GetLastError();
		if (lastError != ERROR_CLASS_ALREADY_EXISTS)
		{
			return HRESULT_FROM_WIN32(lastError);
		}
	}

	hwnd_ = CreateWindowExW(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
		WindowClassName,
		L"LuvLetter Overlay",
		WS_POPUP,
		startX_,
		startY_,
		OverlayWidth,
		OverlayHeight,
		nullptr,
		nullptr,
		GetModuleHandleW(nullptr),
		this);

	if (hwnd_ == nullptr)
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	animationStartTick_ = GetTickCount64();
	animationCompleted_ = false;

	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
	UpdateWindow(hwnd_);

	if (SetTimer(hwnd_, AnimationTimerId, 16, nullptr) == 0)
	{
		return cleanupAndReturn(HRESULT_FROM_WIN32(GetLastError()));
	}

	InvalidateRect(hwnd_, nullptr, FALSE);

	return S_OK;
}

void OverlayHost::Render()
{
	const auto hr = CreateDeviceResources();
	if (FAILED(hr))
	{
		return;
	}

	renderTarget_->BeginDraw();
	renderTarget_->Clear(D2D1::ColorF(0.38f, 0.38f, 0.38f, 1.0f));

	const auto bitmapSize = renderTarget_->GetSize();
	const float left = (bitmapSize.width - LogoDrawSize) * 0.5f;
	const float top = (bitmapSize.height - LogoDrawSize) * 0.5f;
	const auto destination = D2D1::RectF(left, top, left + LogoDrawSize, top + LogoDrawSize);
	renderTarget_->DrawBitmap(
		logoBitmap_.Get(),
		destination,
		1.0f,
		D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);

	const auto endDrawResult = renderTarget_->EndDraw();
	if (endDrawResult == D2DERR_RECREATE_TARGET)
	{
		DiscardDeviceResources();
	}
}

LRESULT OverlayHost::HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND:
		return 1;
	case WM_NCHITTEST:
		return HTTRANSPARENT;
	case WM_PAINT:
	{
		PAINTSTRUCT paintStruct{};
		BeginPaint(hwnd, &paintStruct);
		Render();
		EndPaint(hwnd, &paintStruct);
		return 0;
	}
	case WM_TIMER:
		if (wParam == AnimationTimerId)
		{
			AdvanceAnimation();
			return 0;
		}

		break;
	case WM_DISPLAYCHANGE:
		if (animationCompleted_)
		{
			RecalculateWindowBounds();
			MoveWindowTo(targetX_, targetY_);
		}

		return 0;
	case WM_SIZE:
		if (renderTarget_)
		{
			const auto width = LOWORD(lParam);
			const auto height = HIWORD(lParam);
			renderTarget_->Resize(D2D1::SizeU(width, height));
		}

		return 0;
	case WM_CLOSE:
		DestroyWindow(hwnd);
		return 0;
	case WM_DESTROY:
		KillTimer(hwnd, AnimationTimerId);
		PostQuitMessage(0);
		return 0;
	default:
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT CALLBACK OverlayHost::WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_NCCREATE)
	{
		const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
		auto* host = static_cast<OverlayHost*>(createStruct->lpCreateParams);
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	auto* host = reinterpret_cast<OverlayHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
	if (host == nullptr)
	{
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return host->HandleMessage(hwnd, message, wParam, lParam);
}
