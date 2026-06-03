#include "OverlayHost.h"

#include <string_view>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")

namespace
{
	constexpr wchar_t WindowClassName[] = L"LuvLetter.Core.OverlayWindow";
	constexpr wchar_t OverlayText[] = L"Hello World";
	constexpr int OverlayWidth = 200;
	constexpr int OverlayHeight = 100;
}

OverlayHost& OverlayHost::Instance()
{
	static OverlayHost instance;
	return instance;
}

HRESULT OverlayHost::Start()
{
	if (running_)
	{
		return S_FALSE;
	}

	if (threadHandle_ != nullptr)
	{
		return S_FALSE;
	}

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

	DestroyWindow(hwnd_);
	hwnd_ = nullptr;
	DiscardDeviceResources();
	textFormat_.Reset();
	dwriteFactory_.Reset();
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

	if (!dwriteFactory_)
	{
		auto hr = DWriteCreateFactory(
			DWRITE_FACTORY_TYPE_SHARED,
			__uuidof(IDWriteFactory),
			reinterpret_cast<IUnknown**>(dwriteFactory_.GetAddressOf()));
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!textFormat_)
	{
		auto hr = dwriteFactory_->CreateTextFormat(
			L"Segoe UI",
			nullptr,
			DWRITE_FONT_WEIGHT_REGULAR,
			DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			20.0f,
			L"",
			textFormat_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
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

		hr = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(D2D1::ColorF::White),
			textBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

void OverlayHost::DiscardDeviceResources()
{
	textBrush_.Reset();
	renderTarget_.Reset();
}

HRESULT OverlayHost::CreateOverlayWindow()
{
	auto hr = CreateDeviceIndependentResources();
	if (FAILED(hr))
	{
		return hr;
	}

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
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
		WindowClassName,
		L"LuvLetter Overlay",
		WS_POPUP,
		0,
		0,
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

	UpdateWindowPosition();
	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
	UpdateWindow(hwnd_);
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
	renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 1.0f));

	const auto size = renderTarget_->GetSize();
	const auto layoutRect = D2D1::RectF(16.0f, 0.0f, size.width - 16.0f, size.height);
	renderTarget_->DrawTextW(
		OverlayText,
		static_cast<UINT32>(std::size(OverlayText) - 1),
		textFormat_.Get(),
		layoutRect,
		textBrush_.Get(),
		D2D1_DRAW_TEXT_OPTIONS_NONE,
		DWRITE_MEASURING_MODE_NATURAL);

	const auto endDrawResult = renderTarget_->EndDraw();
	if (endDrawResult == D2DERR_RECREATE_TARGET)
	{
		DiscardDeviceResources();
	}
}

void OverlayHost::UpdateWindowPosition() const
{
	POINT anchorPoint{ 0, GetSystemMetrics(SM_CYSCREEN) - 1 };
	const auto monitor = MonitorFromPoint(anchorPoint, MONITOR_DEFAULTTOPRIMARY);

	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	GetMonitorInfoW(monitor, &monitorInfo);

	const auto x = monitorInfo.rcWork.left;
	const auto y = monitorInfo.rcWork.bottom - OverlayHeight;

	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		x,
		y,
		OverlayWidth,
		OverlayHeight,
		SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

LRESULT OverlayHost::HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND:
		return 1;
	case WM_PAINT:
	{
		PAINTSTRUCT paintStruct{};
		BeginPaint(hwnd, &paintStruct);
		Render();
		EndPaint(hwnd, &paintStruct);
		return 0;
	}
	case WM_DISPLAYCHANGE:
		UpdateWindowPosition();
		InvalidateRect(hwnd, nullptr, FALSE);
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
		PostQuitMessage(0);
		return 0;
	default:
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}
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
