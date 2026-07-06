#include "input/InputBoxHost.h"

#include <imm.h>
#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <cwctype>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "imm32.lib")

namespace
{
	constexpr wchar_t WindowClassName[] = L"LuvLetter.Native.InputBox";
	constexpr UINT InputBoxCommandMessage = WM_APP + 1;
	constexpr UINT_PTR CaretTimerId = 1;
	constexpr UINT CaretBlinkMs = 530;
	constexpr size_t MaxHistoryItems = 100;
	constexpr wchar_t PlaceholderText[] = L"Enter command here";

	enum class InputBoxCommand : WPARAM
	{
		Show = 1,
		Hide = 2,
		Toggle = 3,
		Shutdown = 4,
	};

	LuvLetterInputBoxConfig CreateDefaultConfig()
	{
		LuvLetterInputBoxConfig config{};
		config.width = 640;
		config.height = 44;
		config.cornerRadius = 8.0f;
		config.borderThickness = 2.0f;
		config.fontSize = 20.0f;
		config.horizontalPadding = 10.0f;
		config.verticalPadding = 6.0f;
		config.caretWidth = 2.25f;
		config.positionMode = 0;
		config.bottomMargin = 60;
		config.borderColor = 0xFFFFFFFF;
		config.backgroundColor = 0x38F5F5F5;
		config.textColor = 0xFFFFFFFF;
		config.caretColor = 0xFFFFFFFF;
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

	D2D1_COLOR_F PlaceholderColorFromArgb(uint32_t argb)
	{
		auto color = ColorFromArgb(argb);
		color.a *= 0.48f;
		return color;
	}

	D2D1_RECT_F CreateInputRect(const LuvLetterInputBoxConfig& config)
	{
		return D2D1::RectF(1.0f, 1.0f, config.width - 1.0f, config.height - 1.0f);
	}

	D2D1_RECT_F CreateTextRect(const LuvLetterInputBoxConfig& config)
	{
		const auto horizontalPadding = (std::min)(
			(std::max)(0.0f, config.horizontalPadding),
			(std::max)(0.0f, static_cast<float>(config.width) / 2.0f - 1.0f));
		const auto verticalPadding = (std::min)(
			(std::max)(0.0f, config.verticalPadding),
			(std::max)(0.0f, static_cast<float>(config.height) / 2.0f - 1.0f));
		return D2D1::RectF(
			horizontalPadding,
			verticalPadding,
			static_cast<float>(config.width) - horizontalPadding,
			static_cast<float>(config.height) - verticalPadding);
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
		UpdateWindowShape();
		UpdateWindowPosition();
		Render();
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
	caretIndex_ = 0;

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
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
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

	UpdateWindowShape();
	return S_OK;
}

void InputBoxHost::UpdateWindowShape() const
{
	if (hwnd_ == nullptr)
	{
		return;
	}

	const auto maxRadius = static_cast<float>((std::min)(config_.width, config_.height)) / 2.0f;
	const auto radius = static_cast<int>(std::round((std::min)(config_.cornerRadius, maxRadius)));
	if (radius <= 0)
	{
		SetWindowRgn(hwnd_, nullptr, TRUE);
		return;
	}

	const auto region = CreateWindowRegion();
	if (region == nullptr)
	{
		return;
	}

	if (SetWindowRgn(hwnd_, region, TRUE) == 0)
	{
		DeleteObject(region);
	}
}

HRGN InputBoxHost::CreateWindowRegion() const
{
	const auto maxRadius = static_cast<float>((std::min)(config_.width, config_.height)) / 2.0f;
	const auto radius = static_cast<int>(std::round((std::min)(config_.cornerRadius, maxRadius)));
	if (radius <= 0)
	{
		return nullptr;
	}

	const auto diameter = (std::max)(1, radius * 2);
	return CreateRoundRectRgn(
		0,
		0,
		config_.width,
		config_.height,
		diameter,
		diameter);
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
		const auto renderTargetProperties = D2D1::RenderTargetProperties(
			D2D1_RENDER_TARGET_TYPE_DEFAULT,
			D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
		auto hr = d2dFactory_->CreateDCRenderTarget(
			&renderTargetProperties,
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

	if (!backgroundBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.backgroundColor),
			backgroundBrush_.GetAddressOf());
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

	if (!placeholderBrush_)
	{
		auto hr = renderTarget_->CreateSolidColorBrush(
			PlaceholderColorFromArgb(config_.textColor),
			placeholderBrush_.GetAddressOf());
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
	backgroundBrush_.Reset();
	caretBrush_.Reset();
	placeholderBrush_.Reset();
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

	ResetInput();
	caretVisible_ = true;
	visible_ = true;
	UpdateWindowPosition();
	ShowWindow(hwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(hwnd_);
	SetFocus(hwnd_);
	SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
	Render();
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

void InputBoxHost::ResetInput()
{
	text_.clear();
	caretIndex_ = 0;
	historyIndex_ = -1;
	historyDraft_.clear();
}

void InputBoxHost::SubmitInput()
{
	if (!text_.empty())
	{
		if (history_.empty() || history_.back() != text_)
		{
			history_.push_back(text_);
			if (history_.size() > MaxHistoryItems)
			{
				history_.erase(history_.begin());
			}
		}
	}

	historyIndex_ = -1;
	historyDraft_.clear();
	text_.clear();
	caretIndex_ = 0;
	InvalidateInput();
}

void InputBoxHost::InsertText(const std::wstring& value)
{
	if (value.empty())
	{
		return;
	}

	text_.insert(caretIndex_, value);
	caretIndex_ += value.size();
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::InsertCharacter(wchar_t value)
{
	text_.insert(caretIndex_, 1, value);
	++caretIndex_;
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::DeleteBeforeCaret()
{
	if (caretIndex_ == 0 || text_.empty())
	{
		return;
	}

	text_.erase(caretIndex_ - 1, 1);
	--caretIndex_;
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::DeleteAtCaret()
{
	if (caretIndex_ >= text_.size())
	{
		return;
	}

	text_.erase(caretIndex_, 1);
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::MoveCaretLeft()
{
	if (caretIndex_ == 0)
	{
		return;
	}

	--caretIndex_;
	InvalidateInput();
}

void InputBoxHost::MoveCaretRight()
{
	if (caretIndex_ >= text_.size())
	{
		return;
	}

	++caretIndex_;
	InvalidateInput();
}

void InputBoxHost::MoveCaretToStart()
{
	if (caretIndex_ == 0)
	{
		return;
	}

	caretIndex_ = 0;
	InvalidateInput();
}

void InputBoxHost::MoveCaretToEnd()
{
	if (caretIndex_ == text_.size())
	{
		return;
	}

	caretIndex_ = text_.size();
	InvalidateInput();
}

void InputBoxHost::NavigateHistory(int direction)
{
	if (history_.empty())
	{
		return;
	}

	if (historyIndex_ < 0)
	{
		if (direction > 0)
		{
			return;
		}

		historyDraft_ = text_;
		historyIndex_ = static_cast<int>(history_.size()) - 1;
	}
	else
	{
		historyIndex_ += direction;
	}

	if (historyIndex_ < 0)
	{
		historyIndex_ = 0;
	}

	if (historyIndex_ >= static_cast<int>(history_.size()))
	{
		historyIndex_ = -1;
		text_ = historyDraft_;
		historyDraft_.clear();
	}
	else
	{
		text_ = history_[historyIndex_];
	}

	caretIndex_ = text_.size();
	InvalidateInput();
}

void InputBoxHost::PasteFromClipboard()
{
	if (hwnd_ == nullptr || !OpenClipboard(hwnd_))
	{
		return;
	}

	std::wstring pastedText;
	const auto handle = GetClipboardData(CF_UNICODETEXT);
	if (handle != nullptr)
	{
		const auto* data = static_cast<const wchar_t*>(GlobalLock(handle));
		if (data != nullptr)
		{
			for (const auto* current = data; *current != L'\0'; ++current)
			{
				if (*current == L'\r' || *current == L'\n' || *current == L'\t')
				{
					pastedText.push_back(L' ');
				}
				else if (*current >= 0x20)
				{
					pastedText.push_back(*current);
				}
			}

			GlobalUnlock(handle);
		}
	}

	CloseClipboard();
	InsertText(pastedText);
}

void InputBoxHost::SetCaretFromPoint(LPARAM lParam)
{
	if (text_.empty() || FAILED(EnsureResources()))
	{
		caretIndex_ = text_.size();
		InvalidateInput();
		return;
	}

	const auto textRect = CreateTextRect(config_);
	Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout;
	if (FAILED(dwriteFactory_->CreateTextLayout(
			text_.c_str(),
			static_cast<UINT32>(text_.size()),
			textFormat_.Get(),
			textRect.right - textRect.left,
			textRect.bottom - textRect.top,
			textLayout.GetAddressOf())))
	{
		return;
	}

	BOOL isTrailingHit = FALSE;
	BOOL isInside = FALSE;
	DWRITE_HIT_TEST_METRICS metrics{};
	const auto x = static_cast<float>(GET_X_LPARAM(lParam)) - textRect.left;
	const auto y = static_cast<float>(GET_Y_LPARAM(lParam)) - textRect.top;
	if (SUCCEEDED(textLayout->HitTestPoint(x, y, &isTrailingHit, &isInside, &metrics)))
	{
		caretIndex_ = metrics.textPosition + (isTrailingHit ? metrics.length : 0);
		caretIndex_ = (std::min)(caretIndex_, text_.size());
		InvalidateInput();
	}
}

void InputBoxHost::InvalidateInput()
{
	caretVisible_ = true;
	if (hwnd_ != nullptr)
	{
		UpdateImeCompositionWindow();
		SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
		Render();
	}
}

void InputBoxHost::UpdateImeCompositionWindow()
{
	if (hwnd_ == nullptr || FAILED(EnsureResources()))
	{
		return;
	}

	const auto inputContext = ImmGetContext(hwnd_);
	if (inputContext == nullptr)
	{
		return;
	}

	const auto textRect = CreateTextRect(config_);
	COMPOSITIONFORM compositionForm{};
	compositionForm.dwStyle = CFS_POINT;
	compositionForm.ptCurrentPos.x = static_cast<LONG>(std::round(GetCaretX(textRect)));
	compositionForm.ptCurrentPos.y = static_cast<LONG>(
		std::round((textRect.top + textRect.bottom + config_.fontSize) / 2.0f));
	ImmSetCompositionWindow(inputContext, &compositionForm);
	ImmReleaseContext(hwnd_, inputContext);
}

float InputBoxHost::GetCaretX(const D2D1_RECT_F& textRect)
{
	if (text_.empty() || caretIndex_ == 0)
	{
		return textRect.left;
	}

	Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout;
	if (FAILED(dwriteFactory_->CreateTextLayout(
			text_.c_str(),
			static_cast<UINT32>(text_.size()),
			textFormat_.Get(),
			textRect.right - textRect.left,
			textRect.bottom - textRect.top,
			textLayout.GetAddressOf())))
	{
		return textRect.left;
	}

	DWRITE_HIT_TEST_METRICS caretMetrics{};
	float pointX = 0.0f;
	float pointY = 0.0f;
	const auto safeCaretIndex = (std::min)(caretIndex_, text_.size());
	if (SUCCEEDED(textLayout->HitTestTextPosition(
			static_cast<UINT32>(safeCaretIndex),
			FALSE,
			&pointX,
			&pointY,
			&caretMetrics)))
	{
		return textRect.left + pointX;
	}

	return textRect.left;
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
	if (hwnd_ == nullptr || FAILED(EnsureResources()))
	{
		return;
	}

	const auto width = (std::max)(1, config_.width);
	const auto height = (std::max)(1, config_.height);
	const auto screenDc = GetDC(nullptr);
	if (screenDc == nullptr)
	{
		return;
	}

	auto memoryDc = CreateCompatibleDC(screenDc);
	BITMAPINFO bitmapInfo{};
	bitmapInfo.bmiHeader.biSize = sizeof(bitmapInfo.bmiHeader);
	bitmapInfo.bmiHeader.biWidth = width;
	bitmapInfo.bmiHeader.biHeight = -height;
	bitmapInfo.bmiHeader.biPlanes = 1;
	bitmapInfo.bmiHeader.biBitCount = 32;
	bitmapInfo.bmiHeader.biCompression = BI_RGB;

	void* bitmapBits = nullptr;
	auto bitmap = CreateDIBSection(
		screenDc,
		&bitmapInfo,
		DIB_RGB_COLORS,
		&bitmapBits,
		nullptr,
		0);
	ReleaseDC(nullptr, screenDc);

	if (memoryDc == nullptr || bitmap == nullptr)
	{
		if (bitmap != nullptr)
		{
			DeleteObject(bitmap);
		}
		if (memoryDc != nullptr)
		{
			DeleteDC(memoryDc);
		}
		return;
	}

	const auto previousBitmap = SelectObject(memoryDc, bitmap);
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(renderTarget_->BindDC(memoryDc, &bindRect)))
	{
		SelectObject(memoryDc, previousBitmap);
		DeleteObject(bitmap);
		DeleteDC(memoryDc);
		return;
	}

	renderTarget_->BeginDraw();
	renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.0f));

	const auto roundedRect = D2D1::RoundedRect(
		CreateInputRect(config_),
		config_.cornerRadius,
		config_.cornerRadius);
	renderTarget_->FillRoundedRectangle(roundedRect, backgroundBrush_.Get());
	renderTarget_->DrawRoundedRectangle(roundedRect, borderBrush_.Get(), config_.borderThickness);

	const auto textRect = CreateTextRect(config_);
	const auto* displayText = text_.empty() ? PlaceholderText : text_.c_str();
	const auto displayTextLength = text_.empty()
		? static_cast<UINT32>((sizeof(PlaceholderText) / sizeof(PlaceholderText[0])) - 1)
		: static_cast<UINT32>(text_.size());
	auto* displayBrush = text_.empty() ? placeholderBrush_.Get() : textBrush_.Get();
	renderTarget_->DrawTextW(
		displayText,
		displayTextLength,
		textFormat_.Get(),
		textRect,
		displayBrush,
		D2D1_DRAW_TEXT_OPTIONS_CLIP,
		DWRITE_MEASURING_MODE_NATURAL);

	if (caretVisible_)
	{
		const auto caretX = GetCaretX(textRect);
		const auto textRectHeight = (std::max)(1.0f, textRect.bottom - textRect.top);
		const auto caretHeight = (std::min)(textRectHeight, (std::max)(1.0f, config_.fontSize * 1.1f));
		const auto caretTop = textRect.top + (textRectHeight - caretHeight) / 2.0f;
		const auto caretWidth = (std::max)(0.5f, config_.caretWidth);

		renderTarget_->FillRectangle(
			D2D1::RectF(
				caretX,
				caretTop,
				caretX + caretWidth,
				caretTop + caretHeight),
			caretBrush_.Get());
	}

	const auto endDrawResult = renderTarget_->EndDraw();
	if (endDrawResult == D2DERR_RECREATE_TARGET)
	{
		DiscardResources();
	}
	else if (SUCCEEDED(endDrawResult))
	{
		POINT sourcePoint{ 0, 0 };
		SIZE windowSize{ width, height };
		BLENDFUNCTION blend{};
		blend.BlendOp = AC_SRC_OVER;
		blend.SourceConstantAlpha = 255;
		blend.AlphaFormat = AC_SRC_ALPHA;
		UpdateLayeredWindow(
			hwnd_,
			nullptr,
			nullptr,
			&windowSize,
			memoryDc,
			&sourcePoint,
			0,
			&blend,
			ULW_ALPHA);
	}

	SelectObject(memoryDc, previousBitmap);
	DeleteObject(bitmap);
	DeleteDC(memoryDc);
}

void InputBoxHost::Resize(UINT width, UINT height)
{
	Render();
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
	sanitized.verticalPadding = (std::max)(0.0f, config.verticalPadding);
	sanitized.caretWidth = (std::max)(0.5f, config.caretWidth);
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
		UpdateImeCompositionWindow();
		Render();
		return 0;
	case WM_KILLFOCUS:
		caretVisible_ = false;
		KillTimer(hwnd, CaretTimerId);
		Render();
		return 0;
	case WM_TIMER:
		if (wParam == CaretTimerId)
		{
			caretVisible_ = !caretVisible_;
			Render();
			return 0;
		}
		break;
	case WM_KEYDOWN:
		if ((wParam == L'V' || wParam == L'v') && IsKeyDown(VK_CONTROL))
		{
			PasteFromClipboard();
			return 0;
		}
		if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
		{
			HideWindow();
			return 0;
		}
		if (MatchesHotkey(wParam, config_.submitVirtualKey, config_.submitModifiers))
		{
			SubmitInput();
			return 0;
		}
		if (MatchesHotkey(wParam, config_.backspaceVirtualKey, config_.backspaceModifiers))
		{
			DeleteBeforeCaret();
			return 0;
		}
		if (GetCurrentHotkeyModifiers() == 0)
		{
			switch (wParam)
			{
			case VK_DELETE:
				DeleteAtCaret();
				return 0;
			case VK_LEFT:
				MoveCaretLeft();
				return 0;
			case VK_RIGHT:
				MoveCaretRight();
				return 0;
			case VK_HOME:
				MoveCaretToStart();
				return 0;
			case VK_END:
				MoveCaretToEnd();
				return 0;
			case VK_UP:
				NavigateHistory(-1);
				return 0;
			case VK_DOWN:
				NavigateHistory(1);
				return 0;
			default:
				break;
			}
		}

		break;
	case WM_CHAR:
		if (wParam == L'\b' || wParam == L'\r' || wParam == L'\n' || wParam == L'\t')
		{
			return 0;
		}
		if (wParam >= 0x20)
		{
			InsertCharacter(static_cast<wchar_t>(wParam));
		}

		return 0;
	case WM_PASTE:
		PasteFromClipboard();
		return 0;
	case WM_LBUTTONDOWN:
		SetFocus(hwnd);
		SetCaretFromPoint(lParam);
		return 0;
	case WM_IME_STARTCOMPOSITION:
		UpdateImeCompositionWindow();
		break;
	case WM_IME_COMPOSITION:
		UpdateImeCompositionWindow();
		if ((lParam & GCS_RESULTSTR) != 0)
		{
			const auto inputContext = ImmGetContext(hwnd);
			if (inputContext != nullptr)
			{
				const auto byteCount = ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, nullptr, 0);
				if (byteCount > 0)
				{
					std::wstring result(static_cast<size_t>(byteCount) / sizeof(wchar_t), L'\0');
					ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, result.data(), byteCount);
					InsertText(result);
				}

				ImmReleaseContext(hwnd, inputContext);
			}

			return 0;
		}
		break;
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
