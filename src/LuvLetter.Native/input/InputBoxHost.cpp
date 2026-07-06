#include "input/InputBoxHost.h"

#include <dwmapi.h>

#include <algorithm>
#include <cmath>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dwmapi.lib")

namespace
{
	constexpr wchar_t WindowClassName[] = L"LuvLetter.Native.InputBox";
	constexpr UINT InputBoxCommandMessage = WM_APP + 1;
	constexpr UINT_PTR CaretTimerId = 1;
	constexpr UINT CaretBlinkMs = 530;

	enum class InputBoxCommand : WPARAM
	{
		Show = 1,
		Hide = 2,
		Toggle = 3,
		Shutdown = 4,
	};

	enum WindowCompositionAttribute
	{
		WcaAccentPolicy = 19,
	};

	enum AccentState
	{
		AccentDisabled = 0,
		AccentEnableBlurBehind = 3,
	};

	struct AccentPolicy
	{
		int accentState;
		int accentFlags;
		int gradientColor;
		int animationId;
	};

	struct WindowCompositionAttributeData
	{
		WindowCompositionAttribute attribute;
		void* data;
		size_t dataSize;
	};

	using SetWindowCompositionAttributeProc = BOOL(WINAPI*)(
		HWND hwnd,
		WindowCompositionAttributeData* data);

	LuvLetterInputBoxConfig CreateDefaultConfig()
	{
		LuvLetterInputBoxConfig config{};
		config.width = 640;
		config.height = 56;
		config.cornerRadius = 8.0f;
		config.borderThickness = 2.0f;
		config.fontSize = 20.0f;
		config.horizontalPadding = 18.0f;
		config.positionMode = 0;
		config.bottomMargin = 120;
		config.borderColor = 0xFFFFFFFF;
		config.backgroundColor = 0x66DCDCDC;
		config.textColor = 0xF2191919;
		config.caretColor = 0xF2191919;
		config.submitVirtualKey = VK_RETURN;
		config.cancelVirtualKey = VK_ESCAPE;
		config.backspaceVirtualKey = VK_BACK;
		return config;
	}

	D2D1_COLOR_F ColorFromArgb(uint32_t argb)
	{
		return D2D1::ColorF(
			static_cast<float>((argb >> 16) & 0xFF) / 255.0f,
			static_cast<float>((argb >> 8) & 0xFF) / 255.0f,
			static_cast<float>(argb & 0xFF) / 255.0f,
			static_cast<float>((argb >> 24) & 0xFF) / 255.0f);
	}

	D2D1_RECT_F CreateInputRect(const LuvLetterInputBoxConfig& config)
	{
		return D2D1::RectF(1.0f, 1.0f, config.width - 1.0f, config.height - 1.0f);
	}

	D2D1_RECT_F CreateTextRect(const LuvLetterInputBoxConfig& config)
	{
		const auto padding = (std::max)(0.0f, config.horizontalPadding);
		return D2D1::RectF(
			padding,
			0.0f,
			static_cast<float>(config.width) - padding,
			static_cast<float>(config.height));
	}

	bool IsKeyDown(int virtualKey)
	{
		return (GetKeyState(virtualKey) & 0x8000) != 0;
	}

	int GetCurrentHotkeyModifiers()
	{
		int modifiers = 0;
		if (IsKeyDown(VK_MENU))
		{
			modifiers |= 1;
		}
		if (IsKeyDown(VK_CONTROL))
		{
			modifiers |= 2;
		}
		if (IsKeyDown(VK_SHIFT))
		{
			modifiers |= 4;
		}
		if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
		{
			modifiers |= 8;
		}

		return modifiers;
	}

	bool MatchesHotkey(WPARAM wParam, int virtualKey, int modifiers)
	{
		return wParam == static_cast<WPARAM>(virtualKey)
			&& GetCurrentHotkeyModifiers() == modifiers;
	}
}

InputBoxHost& InputBoxHost::Instance()
{
	static InputBoxHost instance;
	return instance;
}

InputBoxHost::InputBoxHost()
	: config_(CreateDefaultConfig())
{
}

HRESULT InputBoxHost::ApplyConfig(const LuvLetterInputBoxConfig& config)
{
	config_ = SanitizeConfig(config);
	DiscardResources();

	if (hwnd_ != nullptr)
	{
		UpdateWindowPosition();
		InvalidateRect(hwnd_, nullptr, FALSE);
	}

	return S_OK;
}

HRESULT InputBoxHost::Show()
{
	const auto hr = EnsureThread();
	if (FAILED(hr))
	{
		return hr;
	}

	PostMessageW(hwnd_, InputBoxCommandMessage, static_cast<WPARAM>(InputBoxCommand::Show), 0);
	return S_OK;
}

HRESULT InputBoxHost::Hide()
{
	if (hwnd_ == nullptr)
	{
		return S_OK;
	}

	PostMessageW(hwnd_, InputBoxCommandMessage, static_cast<WPARAM>(InputBoxCommand::Hide), 0);
	return S_OK;
}

HRESULT InputBoxHost::Toggle()
{
	const auto hr = EnsureThread();
	if (FAILED(hr))
	{
		return hr;
	}

	PostMessageW(hwnd_, InputBoxCommandMessage, static_cast<WPARAM>(InputBoxCommand::Toggle), 0);
	return S_OK;
}

void InputBoxHost::Shutdown()
{
	if (threadHandle_ == nullptr)
	{
		return;
	}

	if (hwnd_ != nullptr)
	{
		PostMessageW(hwnd_, InputBoxCommandMessage, static_cast<WPARAM>(InputBoxCommand::Shutdown), 0);
	}

	WaitForSingleObject(threadHandle_, INFINITE);
	CloseHandle(threadHandle_);
	threadHandle_ = nullptr;
	threadId_ = 0;
}

HRESULT InputBoxHost::EnsureThread()
{
	if (threadHandle_ != nullptr)
	{
		return S_OK;
	}

	startedEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (startedEvent_ == nullptr)
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	startResult_ = E_FAIL;
	threadHandle_ = CreateThread(nullptr, 0, &InputBoxHost::ThreadEntry, this, 0, &threadId_);
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

DWORD WINAPI InputBoxHost::ThreadEntry(LPVOID parameter)
{
	auto* host = static_cast<InputBoxHost*>(parameter);
	host->startResult_ = host->Run();
	if (host->startedEvent_ != nullptr)
	{
		SetEvent(host->startedEvent_);
	}

	return static_cast<DWORD>(FAILED(host->startResult_) ? host->startResult_ : 0);
}

HRESULT InputBoxHost::Run()
{
	const auto comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	const auto shouldUninitialize = SUCCEEDED(comResult);

	auto hr = CreateInputWindow();
	if (FAILED(hr))
	{
		startResult_ = hr;
		if (shouldUninitialize)
		{
			CoUninitialize();
		}

		return hr;
	}

	startResult_ = S_OK;
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

	DiscardResources();
	hwnd_ = nullptr;
	visible_ = false;
	text_.clear();

	if (shouldUninitialize)
	{
		CoUninitialize();
	}

	return S_OK;
}

HRESULT InputBoxHost::CreateInputWindow()
{
	WNDCLASSEXW windowClass{};
	windowClass.cbSize = sizeof(windowClass);
	windowClass.lpfnWndProc = &InputBoxHost::WindowProc;
	windowClass.hInstance = GetModuleHandleW(nullptr);
	windowClass.lpszClassName = WindowClassName;
	windowClass.hCursor = LoadCursorW(nullptr, IDC_IBEAM);

	if (RegisterClassExW(&windowClass) == 0)
	{
		const auto lastError = GetLastError();
		if (lastError != ERROR_CLASS_ALREADY_EXISTS)
		{
			return HRESULT_FROM_WIN32(lastError);
		}
	}

	hwnd_ = CreateWindowExW(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
		WindowClassName,
		L"LuvLetter Input",
		WS_POPUP,
		CW_USEDEFAULT,
		CW_USEDEFAULT,
		config_.width,
		config_.height,
		nullptr,
		nullptr,
		GetModuleHandleW(nullptr),
		this);

	if (hwnd_ == nullptr)
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	EnableBlur();
	return S_OK;
}

void InputBoxHost::EnableBlur() const
{
	if (hwnd_ == nullptr)
	{
		return;
	}

	const auto user32 = GetModuleHandleW(L"user32.dll");
	if (user32 != nullptr)
	{
		const auto setWindowCompositionAttribute =
			reinterpret_cast<SetWindowCompositionAttributeProc>(
				GetProcAddress(user32, "SetWindowCompositionAttribute"));
		if (setWindowCompositionAttribute != nullptr)
		{
			AccentPolicy accent{};
			accent.accentState = AccentEnableBlurBehind;
			accent.gradientColor = 0x66E6E6E6;

			WindowCompositionAttributeData data{};
			data.attribute = WcaAccentPolicy;
			data.data = &accent;
			data.dataSize = sizeof(accent);
			setWindowCompositionAttribute(hwnd_, &data);
			return;
		}
	}

	DWM_BLURBEHIND blurBehind{};
	blurBehind.dwFlags = DWM_BB_ENABLE;
	blurBehind.fEnable = TRUE;
	DwmEnableBlurBehindWindow(hwnd_, &blurBehind);
}

HRESULT InputBoxHost::EnsureResources()
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

	if (!renderTarget_)
	{
		auto hr = d2dFactory_->CreateHwndRenderTarget(
			D2D1::RenderTargetProperties(),
			D2D1::HwndRenderTargetProperties(
				hwnd_,
				D2D1::SizeU(config_.width, config_.height),
				D2D1_PRESENT_OPTIONS_IMMEDIATELY),
			renderTarget_.GetAddressOf());
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
			config_.fontSize,
			L"",
			textFormat_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
	}

	if (!fillBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.backgroundColor),
			fillBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!borderBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.borderColor),
			borderBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!textBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.textColor),
			textBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!caretBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.caretColor),
			caretBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

void InputBoxHost::DiscardResources()
{
	textBrush_.Reset();
	borderBrush_.Reset();
	fillBrush_.Reset();
	caretBrush_.Reset();
	textFormat_.Reset();
	renderTarget_.Reset();
	dwriteFactory_.Reset();
	d2dFactory_.Reset();
}

void InputBoxHost::ShowWindowAndFocus()
{
	if (hwnd_ == nullptr)
	{
		return;
	}

	text_.clear();
	caretVisible_ = true;
	visible_ = true;
	UpdateWindowPosition();
	ShowWindow(hwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(hwnd_);
	SetFocus(hwnd_);
	SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
	InvalidateRect(hwnd_, nullptr, FALSE);
}

void InputBoxHost::HideWindow()
{
	if (hwnd_ == nullptr)
	{
		return;
	}

	visible_ = false;
	KillTimer(hwnd_, CaretTimerId);
	ShowWindow(hwnd_, SW_HIDE);
}

void InputBoxHost::UpdateWindowPosition() const
{
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	const auto monitor = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
	GetMonitorInfoW(monitor, &monitorInfo);

	const auto workWidth = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
	const auto workHeight = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;
	LONG x = monitorInfo.rcWork.left +
		((std::max)(0L, workWidth - static_cast<LONG>(config_.width)) / 2);
	LONG y = monitorInfo.rcWork.bottom - static_cast<LONG>(config_.height) - config_.bottomMargin;

	switch (config_.positionMode)
	{
	case 1:
		y = monitorInfo.rcWork.top +
			((std::max)(0L, workHeight - static_cast<LONG>(config_.height)) / 2);
		break;
	case 2:
		y = monitorInfo.rcWork.top + config_.bottomMargin;
		break;
	case 3:
		x = monitorInfo.rcWork.left + config_.customX;
		y = monitorInfo.rcWork.top + config_.customY;
		break;
	default:
		break;
	}

	x += config_.offsetX;
	y += config_.offsetY;

	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		x,
		y,
		config_.width,
		config_.height,
		SWP_NOACTIVATE);
}

void InputBoxHost::Render()
{
	if (FAILED(EnsureResources()))
	{
		return;
	}

	renderTarget_->BeginDraw();
	renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.0f));

	const auto roundedRect = D2D1::RoundedRect(
		CreateInputRect(config_),
		config_.cornerRadius,
		config_.cornerRadius);
	renderTarget_->FillRoundedRectangle(roundedRect, fillBrush_.Get());
	renderTarget_->DrawRoundedRectangle(roundedRect, borderBrush_.Get(), config_.borderThickness);

	const auto textRect = CreateTextRect(config_);
	if (!text_.empty())
	{
		renderTarget_->DrawTextW(
			text_.c_str(),
			static_cast<UINT32>(text_.size()),
			textFormat_.Get(),
			textRect,
			textBrush_.Get(),
			D2D1_DRAW_TEXT_OPTIONS_CLIP,
			DWRITE_MEASURING_MODE_NATURAL);
	}

	if (caretVisible_)
	{
		auto caretX = textRect.left;
		if (!text_.empty())
		{
			Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout;
			if (SUCCEEDED(dwriteFactory_->CreateTextLayout(
					text_.c_str(),
					static_cast<UINT32>(text_.size()),
					textFormat_.Get(),
					textRect.right - textRect.left,
					textRect.bottom - textRect.top,
					textLayout.GetAddressOf())))
			{
				DWRITE_HIT_TEST_METRICS caretMetrics{};
				float pointX = 0.0f;
				float pointY = 0.0f;
				if (SUCCEEDED(textLayout->HitTestTextPosition(
						static_cast<UINT32>(text_.size()),
						FALSE,
						&pointX,
						&pointY,
						&caretMetrics)))
				{
					caretX = textRect.left + pointX;
				}
			}
		}

		renderTarget_->FillRectangle(
			D2D1::RectF(
				caretX,
				14.0f,
				caretX + 1.5f,
				static_cast<float>(config_.height) - 14.0f),
			caretBrush_.Get());
	}

	const auto endDrawResult = renderTarget_->EndDraw();
	if (endDrawResult == D2DERR_RECREATE_TARGET)
	{
		DiscardResources();
	}
}

void InputBoxHost::Resize(UINT width, UINT height)
{
	if (renderTarget_)
	{
		renderTarget_->Resize(D2D1::SizeU(width, height));
	}
}

LuvLetterInputBoxConfig InputBoxHost::SanitizeConfig(const LuvLetterInputBoxConfig& config)
{
	auto sanitized = CreateDefaultConfig();
	sanitized.width = (std::max)(1, config.width);
	sanitized.height = (std::max)(1, config.height);
	sanitized.cornerRadius = (std::max)(0.0f, config.cornerRadius);
	sanitized.borderThickness = (std::max)(0.0f, config.borderThickness);
	sanitized.fontSize = (std::max)(1.0f, config.fontSize);
	sanitized.horizontalPadding = (std::max)(0.0f, config.horizontalPadding);
	sanitized.positionMode =
		config.positionMode >= 0 && config.positionMode <= 3 ? config.positionMode : sanitized.positionMode;
	sanitized.offsetX = config.offsetX;
	sanitized.offsetY = config.offsetY;
	sanitized.bottomMargin = (std::max)(0, config.bottomMargin);
	sanitized.customX = config.customX;
	sanitized.customY = config.customY;
	sanitized.borderColor = config.borderColor;
	sanitized.backgroundColor = config.backgroundColor;
	sanitized.textColor = config.textColor;
	sanitized.caretColor = config.caretColor;
	sanitized.submitVirtualKey =
		config.submitVirtualKey > 0 ? config.submitVirtualKey : sanitized.submitVirtualKey;
	sanitized.cancelVirtualKey =
		config.cancelVirtualKey > 0 ? config.cancelVirtualKey : sanitized.cancelVirtualKey;
	sanitized.backspaceVirtualKey =
		config.backspaceVirtualKey > 0 ? config.backspaceVirtualKey : sanitized.backspaceVirtualKey;
	sanitized.submitModifiers = config.submitModifiers;
	sanitized.cancelModifiers = config.cancelModifiers;
	sanitized.backspaceModifiers = config.backspaceModifiers;
	return sanitized;
}

LRESULT InputBoxHost::HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case InputBoxCommandMessage:
		if (wParam == static_cast<WPARAM>(InputBoxCommand::Show))
		{
			ShowWindowAndFocus();
			return 0;
		}

		if (wParam == static_cast<WPARAM>(InputBoxCommand::Hide))
		{
			HideWindow();
			return 0;
		}

		if (wParam == static_cast<WPARAM>(InputBoxCommand::Toggle))
		{
			if (visible_)
			{
				HideWindow();
			}
			else
			{
				ShowWindowAndFocus();
			}

			return 0;
		}

		if (wParam == static_cast<WPARAM>(InputBoxCommand::Shutdown))
		{
			DestroyWindow(hwnd);
			return 0;
		}

		break;
	case WM_ERASEBKGND:
		return 1;
	case WM_SETFOCUS:
		caretVisible_ = true;
		SetTimer(hwnd, CaretTimerId, CaretBlinkMs, nullptr);
		InvalidateRect(hwnd, nullptr, FALSE);
		return 0;
	case WM_KILLFOCUS:
		caretVisible_ = false;
		KillTimer(hwnd, CaretTimerId);
		InvalidateRect(hwnd, nullptr, FALSE);
		return 0;
	case WM_TIMER:
		if (wParam == CaretTimerId)
		{
			caretVisible_ = !caretVisible_;
			InvalidateRect(hwnd, nullptr, FALSE);
			return 0;
		}
		break;
	case WM_KEYDOWN:
		if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
		{
			HideWindow();
			return 0;
		}
		if (MatchesHotkey(wParam, config_.submitVirtualKey, config_.submitModifiers))
		{
			HideWindow();
			return 0;
		}
		if (MatchesHotkey(wParam, config_.backspaceVirtualKey, config_.backspaceModifiers))
		{
			if (!text_.empty())
			{
				text_.pop_back();
			}

			caretVisible_ = true;
			InvalidateRect(hwnd, nullptr, FALSE);
			return 0;
		}

		break;
	case WM_CHAR:
		if (wParam == L'\b' || wParam == L'\r')
		{
			return 0;
		}
		if (wParam >= 0x20)
		{
			text_.push_back(static_cast<wchar_t>(wParam));
		}

		caretVisible_ = true;
		InvalidateRect(hwnd, nullptr, FALSE);
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paintStruct{};
		BeginPaint(hwnd, &paintStruct);
		Render();
		EndPaint(hwnd, &paintStruct);
		return 0;
	}
	case WM_SIZE:
		Resize(LOWORD(lParam), HIWORD(lParam));
		return 0;
	case WM_CLOSE:
		HideWindow();
		return 0;
	case WM_DESTROY:
		KillTimer(hwnd, CaretTimerId);
		DiscardResources();
		hwnd_ = nullptr;
		visible_ = false;
		PostQuitMessage(0);
		return 0;
	default:
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT CALLBACK InputBoxHost::WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_NCCREATE)
	{
		const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
		auto* host = static_cast<InputBoxHost*>(createStruct->lpCreateParams);
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
		return TRUE;
	}

	auto* host = reinterpret_cast<InputBoxHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
	if (host == nullptr)
	{
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return host->HandleMessage(hwnd, message, wParam, lParam);
}
