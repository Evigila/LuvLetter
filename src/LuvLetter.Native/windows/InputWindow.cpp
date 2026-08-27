#include "windows/InputWindow.h"

#include "configuration/NativeConfigurationSanitizer.h"

#include <imm.h>
#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <utility>

#pragma comment(lib, "imm32.lib")

using namespace LuvLetterNative;

namespace
{
	constexpr UINT_PTR CaretTimerId = 1;
	constexpr UINT_PTR AnimationTimerId = 2;
	constexpr UINT CaretBlinkMs = 530;
	constexpr UINT AnimationFrameMs = 16;
	constexpr size_t MaxInputCharacters = 32768;
	constexpr size_t HistoryCapacity = 100;
	constexpr float MaxTextLayoutHeight = 16777216.0f;
	constexpr int64_t MaxSurfacePixels = 16LL * 1024LL * 1024LL;
	constexpr wchar_t PlaceholderText[] = L"Enter command here";

	constexpr int SelectLineCapacity(UINT32 lineCount) noexcept
	{
		return lineCount <= 1 ? 1 : lineCount <= 2 ? 2 : lineCount <= 4 ? 4 : 6;
	}

	bool IsHighSurrogate(wchar_t value) noexcept
	{
		return value >= 0xD800 && value <= 0xDBFF;
	}

	bool IsLowSurrogate(wchar_t value) noexcept
	{
		return value >= 0xDC00 && value <= 0xDFFF;
	}

	size_t PreviousUtf16Boundary(const std::wstring& value, size_t index) noexcept
	{
		if (index == 0) return 0;
		if (index >= 2 && IsLowSurrogate(value[index - 1]) && IsHighSurrogate(value[index - 2]))
		{
			return index - 2;
		}
		return index - 1;
	}

	size_t NextUtf16Boundary(const std::wstring& value, size_t index) noexcept
	{
		if (index >= value.size()) return value.size();
		if (index + 1 < value.size()
			&& IsHighSurrogate(value[index])
			&& IsLowSurrogate(value[index + 1]))
		{
			return index + 2;
		}
		return index + 1;
	}

}

InputWindow::InputWindow(
	ID2D1Factory* d2dFactory,
	IDWriteFactory* dwriteFactory,
	std::function<void(const std::wstring&)> submitted)
	: config_(NativeConfigurationSanitizer::DefaultInputBox()),
	submitted_(std::move(submitted)),
	d2dFactory_(d2dFactory),
	dwriteFactory_(dwriteFactory),
	surface_(std::make_unique<LayeredWindowSurface>())
{
}

HRESULT InputWindow::Attach(HWND window)
{
	if (window == nullptr || hwnd_ != nullptr) return E_INVALIDARG;
	hwnd_ = window;
	dpi_ = QueryWindowDpi(hwnd_);
	SetWindowPos(
		hwnd_,
		nullptr,
		0,
		0,
		PixelWidth(),
		PixelHeight(),
		SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
	UpdateWindowShape();
	return S_OK;
}

HRESULT InputWindow::EnsureResources()
{
	HRESULT result = S_OK;
	if (surface_ == nullptr)
	{
		surface_ = std::make_unique<LuvLetterNative::LayeredWindowSurface>();
	}
	result = surface_->Ensure(
		PixelWidth(),
		PixelHeight(),
		MaxSurfacePixels);
	if (FAILED(result))
	{
		return result;
	}
	if (!renderTarget_)
	{
		const auto properties = D2D1::RenderTargetProperties(
			D2D1_RENDER_TARGET_TYPE_DEFAULT,
			D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
		result = d2dFactory_->CreateDCRenderTarget(&properties, renderTarget_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	renderTarget_->SetDpi(static_cast<float>(dpi_), static_cast<float>(dpi_));
	if (!textFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize, L"", textFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
		textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
		result = textFormat_->SetLineSpacing(
			DWRITE_LINE_SPACING_METHOD_UNIFORM,
			LineHeightDip(),
			config_.fontSize);
		if (FAILED(result)) return result;
	}
	if (!borderBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.borderColor), borderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!backgroundBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.backgroundColor), backgroundBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!textBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.textColor), textBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!placeholderBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.textColor), placeholderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!caretBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.caretColor), caretBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!text_.empty() && !textLayout_)
	{
		result = dwriteFactory_->CreateTextLayout(
			text_.c_str(), static_cast<UINT32>(text_.size()), textFormat_.Get(),
			TextWidthDip(), MaxTextLayoutHeight, textLayout_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void InputWindow::DiscardResources(bool discardSurface)
{
	caretDirtyValid_ = false;
	textLayout_.Reset();
	caretBrush_.Reset();
	placeholderBrush_.Reset();
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	textFormat_.Reset();
	renderTarget_.Reset();
	if (discardSurface && surface_ != nullptr)
	{
		surface_->Reset();
	}
}

void InputWindow::ApplyConfiguration(const LuvLetterInputBoxConfig& config)
{
	config_ = NativeConfigurationSanitizer::SanitizeInputBox(config);
	lineCapacity_ = 1;
	verticalOffset_ = 0.0f;
	DiscardResources(true);
	UpdateResponsiveHeight();
	UpdateWindowShape();
	UpdateWindowPosition();
	if (visible_) Render();
}

void InputWindow::UpdateWindowShape() const
{
	if (hwnd_ != nullptr)
	{
		// HRGN coverage is binary and destroys the per-pixel antialiasing produced
		// by D2D. The layered bitmap already supplies the visual and hit-test shape.
		SetWindowRgn(hwnd_, nullptr, TRUE);
	}
}

void InputWindow::RefreshDpiFromWindow()
{
	const auto dpi = QueryWindowDpi(hwnd_);
	if (dpi == dpi_) return;
	dpi_ = dpi;
	DiscardResources(true);
}

void InputWindow::ApplyDpiChange(UINT dpi, const RECT* suggestedRect)
{
	dpi_ = NormalizeDpi(dpi);
	DiscardResources(true);
	if (suggestedRect != nullptr)
	{
		targetMonitor_ = MonitorFromRect(suggestedRect, MONITOR_DEFAULTTONEAREST);
		SetWindowPos(
			hwnd_, nullptr,
			suggestedRect->left, suggestedRect->top,
			suggestedRect->right - suggestedRect->left,
			suggestedRect->bottom - suggestedRect->top,
			SWP_NOZORDER | SWP_NOACTIVATE);
	}
	else
	{
		targetMonitor_ = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
		UpdateWindowPosition();
	}
	UpdateWindowShape();
	UpdateImeCompositionWindow();
	if (visible_) Render();
}

void InputWindow::Show(HMONITOR targetMonitor, HWND previousForegroundWindow)
{
	if (hwnd_ == nullptr) return;
	SynchronizeAnimation();
	const auto wasPresenting = animator_.Current().ShouldPresent();
	previousForegroundHwnd_ = previousForegroundWindow;
	targetMonitor_ = targetMonitor;
	EnableWindow(hwnd_, TRUE);
	Reset();
	caretVisible_ = true;
	// The first move enters the target monitor and lets PMv2 deliver authoritative
	// WM_DPICHANGED data. GetDpiForWindow then verifies it before final placement.
	UpdateWindowPosition(wasPresenting);
	RefreshDpiFromWindow();
	UpdateWindowPosition(wasPresenting);
	UpdateWindowShape();
	visible_ = true;
	animator_.Show();
	caretDirtyValid_ = false;
	UpdateWindowPosition();
	Render();
	ShowWindow(hwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(hwnd_);
	SetFocus(hwnd_);
	SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
	animationTimestamp_ = GetTickCount64();
	if (animator_.Current().IsAnimating())
	{
		if (SetTimer(hwnd_, AnimationTimerId, AnimationFrameMs, nullptr) == 0)
		{
			animator_.Reset(true);
			KillTimer(hwnd_, AnimationTimerId);
			UpdateWindowPosition();
			Render();
		}
	}
	else
	{
		KillTimer(hwnd_, AnimationTimerId);
	}
}

void InputWindow::Hide()
{
	if (hwnd_ == nullptr) return;
	if (!visible_ && !animator_.TargetVisible())
	{
		if (IsWindowEnabled(hwnd_)) EnableWindow(hwnd_, FALSE);
		return;
	}
	SynchronizeAnimation();
	visible_ = false;
	KillTimer(hwnd_, CaretTimerId);
	caretVisible_ = false;
	animator_.Hide();
	caretDirtyValid_ = false;
	ReleaseFocus();
	UpdateWindowPosition();
	Render();
	animationTimestamp_ = GetTickCount64();
	if (animator_.Current().IsAnimating())
	{
		if (SetTimer(hwnd_, AnimationTimerId, AnimationFrameMs, nullptr) == 0)
		{
			animator_.Reset(false);
			UpdateWindowPosition();
			Render();
			CompleteHide();
			return;
		}
		return;
	}
	CompleteHide();
}

void InputWindow::HideImmediately()
{
	if (hwnd_ == nullptr) return;
	visible_ = false;
	caretVisible_ = false;
	KillTimer(hwnd_, CaretTimerId);
	KillTimer(hwnd_, AnimationTimerId);
	animator_.Reset(false);
	animationTimestamp_ = 0;
	caretDirtyValid_ = false;
	EnableWindow(hwnd_, FALSE);
	ShowWindow(hwnd_, SW_HIDE);
}

void InputWindow::ReleaseFocus()
{
	if (hwnd_ == nullptr) return;
	const auto inputOwnedFocus = GetForegroundWindow() == hwnd_
		|| GetFocus() == hwnd_;
	EnableWindow(hwnd_, FALSE);
	if (inputOwnedFocus
		&& peerHwnd_ != nullptr
		&& IsWindowVisible(peerHwnd_)
		&& IsWindowEnabled(peerHwnd_))
	{
		SetForegroundWindow(peerHwnd_);
		SetFocus(peerHwnd_);
	}
	else if (inputOwnedFocus
		&& previousForegroundHwnd_ != nullptr
		&& previousForegroundHwnd_ != hwnd_
		&& previousForegroundHwnd_ != peerHwnd_
		&& IsWindow(previousForegroundHwnd_))
	{
		SetForegroundWindow(previousForegroundHwnd_);
	}
	if (GetFocus() == hwnd_)
	{
		SetFocus(nullptr);
	}
}

void InputWindow::SynchronizeAnimation()
{
	const auto now = GetTickCount64();
	if (animationTimestamp_ != 0 && animator_.Current().IsAnimating())
	{
		const auto elapsed = now >= animationTimestamp_
			? static_cast<double>(now - animationTimestamp_)
			: 0.0;
		animator_.Advance(elapsed);
	}
	animationTimestamp_ = now;
}

void InputWindow::AdvanceAnimation()
{
	if (hwnd_ == nullptr) return;
	SynchronizeAnimation();
	const auto frame = animator_.Current();
	UpdateWindowPosition();
	Render();
	if (frame.IsAnimating()) return;
	KillTimer(hwnd_, AnimationTimerId);
	if (!frame.ShouldPresent())
	{
		CompleteHide();
	}
}

void InputWindow::CompleteHide()
{
	if (hwnd_ == nullptr) return;
	KillTimer(hwnd_, AnimationTimerId);
	ShowWindow(hwnd_, SW_HIDE);
	animationTimestamp_ = 0;
}

void InputWindow::UpdateWindowPosition(bool applyAnimation) const
{
	if (hwnd_ == nullptr) return;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return;
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	const auto bottomMargin = DipToPixels(static_cast<float>(config_.bottomMargin), dpi_);
	const auto workWidth = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
	const auto workHeight = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;
	LONG x = monitorInfo.rcWork.left + ((std::max)(0L, workWidth - static_cast<LONG>(width)) / 2);
	LONG y = monitorInfo.rcWork.bottom - static_cast<LONG>(height) - bottomMargin;
	switch (config_.positionMode)
	{
	case 1:
		y = monitorInfo.rcWork.top + ((std::max)(0L, workHeight - static_cast<LONG>(height)) / 2);
		break;
	case 2:
		y = monitorInfo.rcWork.top + bottomMargin;
		break;
	case 3:
		x = monitorInfo.rcWork.left + DipToPixels(static_cast<float>(config_.customX), dpi_);
		y = monitorInfo.rcWork.top + DipToPixels(static_cast<float>(config_.customY), dpi_);
		break;
	default:
		break;
	}
	x += DipToPixels(static_cast<float>(config_.offsetX), dpi_);
	y += DipToPixels(static_cast<float>(config_.offsetY), dpi_);
	if (applyAnimation)
	{
		y += DipToPixels(animator_.Current().verticalOffsetDip, dpi_);
	}
	SetWindowPos(hwnd_, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
}

void InputWindow::Reset()
{
	text_.clear();
	textLayout_.Reset();
	caretIndex_ = 0;
	lineCapacity_ = 1;
	verticalOffset_ = 0.0f;
	ResetHistoryNavigation();
}

void InputWindow::Submit()
{
	if (!text_.empty() && submitted_)
	{
		submitted_(text_);
	}
	RecordHistory(text_);
	text_.clear();
	textLayout_.Reset();
	caretIndex_ = 0;
	verticalOffset_ = 0.0f;
	Invalidate();
}

void InputWindow::InsertText(const std::wstring& value)
{
	if (value.empty() || text_.size() >= MaxInputCharacters) return;
	const auto count = (std::min)(value.size(), MaxInputCharacters - text_.size());
	auto safeCount = count;
	if (safeCount < value.size() && safeCount > 0
		&& IsHighSurrogate(value[safeCount - 1]) && IsLowSurrogate(value[safeCount]))
	{
		--safeCount;
	}
	if (safeCount == 0) return;
	text_.insert(caretIndex_, value.data(), safeCount);
	textLayout_.Reset();
	caretIndex_ += safeCount;
	ResetHistoryNavigation();
	Invalidate();
}

void InputWindow::InsertCharacter(wchar_t value)
{
	if (text_.size() >= MaxInputCharacters) return;
	// WM_CHAR delivers a supplementary character as two UTF-16 code units. Do not
	// admit the high surrogate into the final slot, where its low surrogate could
	// no longer be appended. Likewise, ignore an unmatched low surrogate.
	if (IsHighSurrogate(value) && MaxInputCharacters - text_.size() < 2) return;
	if (IsLowSurrogate(value)
		&& (caretIndex_ == 0 || !IsHighSurrogate(text_[caretIndex_ - 1]))) return;
	text_.insert(caretIndex_, 1, value);
	textLayout_.Reset();
	++caretIndex_;
	ResetHistoryNavigation();
	Invalidate();
}

void InputWindow::DeleteBeforeCaret()
{
	if (caretIndex_ == 0 || text_.empty()) return;
	const auto previous = PreviousUtf16Boundary(text_, caretIndex_);
	text_.erase(previous, caretIndex_ - previous);
	textLayout_.Reset();
	caretIndex_ = previous;
	ResetHistoryNavigation();
	Invalidate();
}

void InputWindow::DeleteAtCaret()
{
	if (caretIndex_ >= text_.size()) return;
	const auto next = NextUtf16Boundary(text_, caretIndex_);
	text_.erase(caretIndex_, next - caretIndex_);
	textLayout_.Reset();
	ResetHistoryNavigation();
	Invalidate();
}

void InputWindow::MoveCaretLeft()
{
	if (caretIndex_ == 0) return;
	caretIndex_ = PreviousUtf16Boundary(text_, caretIndex_);
	Invalidate();
}

void InputWindow::MoveCaretRight()
{
	if (caretIndex_ >= text_.size()) return;
	caretIndex_ = NextUtf16Boundary(text_, caretIndex_);
	Invalidate();
}

void InputWindow::MoveCaretToStart()
{
	if (caretIndex_ == 0) return;
	caretIndex_ = 0;
	Invalidate();
}

void InputWindow::MoveCaretToEnd()
{
	if (caretIndex_ == text_.size()) return;
	caretIndex_ = text_.size();
	Invalidate();
}

void InputWindow::NavigateHistory(int direction)
{
	if (!TryNavigateHistory(direction, text_)) return;
	textLayout_.Reset();
	caretIndex_ = text_.size();
	Invalidate();
}

void InputWindow::PasteFromClipboard()
{
	if (hwnd_ == nullptr || !OpenClipboard(hwnd_)) return;
	struct ClipboardGuard final
	{
		~ClipboardGuard() noexcept { CloseClipboard(); }
	} clipboardGuard;

	std::wstring pastedText;
	const auto handle = GetClipboardData(CF_UNICODETEXT);
	if (handle != nullptr)
	{
		const auto byteCount = GlobalSize(handle);
		const auto* data = static_cast<const wchar_t*>(GlobalLock(handle));
		if (data != nullptr)
		{
			struct GlobalUnlockGuard final
			{
				HGLOBAL handle;
				~GlobalUnlockGuard() noexcept { GlobalUnlock(handle); }
			} unlockGuard{ handle };

			const auto capacity = static_cast<size_t>(byteCount / sizeof(wchar_t));
			for (size_t index = 0;
				index < capacity && data[index] != L'\0' && pastedText.size() < MaxInputCharacters;
				++index)
			{
				const auto value = data[index];
				if (value == L'\r' || value == L'\n' || value == L'\t') pastedText.push_back(L' ');
				else if (value >= 0x20) pastedText.push_back(value);
			}
			if (!pastedText.empty() && IsHighSurrogate(pastedText.back()))
			{
				pastedText.pop_back();
			}
		}
	}
	InsertText(pastedText);
}

void InputWindow::SetCaretFromPoint(LPARAM lParam)
{
	if (text_.empty() || FAILED(EnsureResources()))
	{
		caretIndex_ = text_.size();
		Invalidate();
		return;
	}
	BOOL trailing = FALSE;
	BOOL inside = FALSE;
	DWRITE_HIT_TEST_METRICS metrics{};
	const auto widthScale = (std::clamp)(
		animator_.Current().widthScale,
		0.0f,
		1.0f);
	const auto animatedWidth = (std::max)(1.0f, static_cast<float>(config_.width) * widthScale);
	const auto animatedLeft = (static_cast<float>(config_.width) - animatedWidth) / 2.0f;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto x = PixelsToDip(GET_X_LPARAM(lParam), dpi_)
		- animatedLeft
		- horizontalPadding;
	const auto textTop = TextTopDip();
	const auto y = PixelsToDip(GET_Y_LPARAM(lParam), dpi_) - textTop + verticalOffset_;
	if (SUCCEEDED(textLayout_->HitTestPoint(x, y, &trailing, &inside, &metrics)))
	{
		caretIndex_ = (std::min)(
			static_cast<size_t>(metrics.textPosition + (trailing ? metrics.length : 0)),
			text_.size());
		if (caretIndex_ > 0 && caretIndex_ < text_.size()
			&& IsHighSurrogate(text_[caretIndex_ - 1]) && IsLowSurrogate(text_[caretIndex_]))
		{
			caretIndex_ = trailing ? caretIndex_ + 1 : caretIndex_ - 1;
		}
		Invalidate();
	}
}

void InputWindow::Invalidate()
{
	caretVisible_ = visible_;
	UpdateResponsiveHeight();
	EnsureCaretVisible();
	if (hwnd_ != nullptr)
	{
		UpdateImeCompositionWindow();
		if (visible_)
		{
			SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
		}
		Render();
	}
}

void InputWindow::UpdateResponsiveHeight()
{
	int nextCapacity = 1;
	if (!text_.empty())
	{
		if (FAILED(EnsureResources())) return;
		DWRITE_TEXT_METRICS metrics{};
		if (!textLayout_ || FAILED(textLayout_->GetMetrics(&metrics))) return;
		nextCapacity = SelectLineCapacity(metrics.lineCount);
	}

	if (nextCapacity == lineCapacity_) return;
	lineCapacity_ = nextCapacity;
	verticalOffset_ = 0.0f;
	DiscardResources(true);
	UpdateWindowPosition();
}

D2D1_POINT_2F InputWindow::GetCaretLogicalPosition()
{
	auto position = D2D1::Point2F(0.0f, 0.0f);
	if (text_.empty() || caretIndex_ == 0 || FAILED(EnsureResources())) return position;
	DWRITE_HIT_TEST_METRICS metrics{};
	if (textLayout_ == nullptr || FAILED(textLayout_->HitTestTextPosition(
		static_cast<UINT32>((std::min)(caretIndex_, text_.size())),
		FALSE,
		&position.x,
		&position.y,
		&metrics)))
	{
		return D2D1::Point2F(0.0f, 0.0f);
	}
	return position;
}

void InputWindow::EnsureCaretVisible()
{
	if (text_.empty() || FAILED(EnsureResources()) || !textLayout_)
	{
		verticalOffset_ = 0.0f;
		return;
	}
	const auto caret = GetCaretLogicalPosition();
	const auto lineHeight = LineHeightDip();
	const auto viewportHeight = TextViewportHeightDip();
	DWRITE_TEXT_METRICS textMetrics{};
	if (FAILED(textLayout_->GetMetrics(&textMetrics))) return;
	if (caret.y < verticalOffset_)
	{
		verticalOffset_ = caret.y;
	}
	else if (caret.y + lineHeight > verticalOffset_ + viewportHeight)
	{
		verticalOffset_ = caret.y + lineHeight - viewportHeight;
	}
	const auto maximumOffset = (std::max)(0.0f, textMetrics.height - viewportHeight);
	verticalOffset_ = (std::clamp)(verticalOffset_, 0.0f, maximumOffset);
}

void InputWindow::UpdateImeCompositionWindow()
{
	if (hwnd_ == nullptr || FAILED(EnsureResources())) return;
	const auto inputContext = ImmGetContext(hwnd_);
	if (inputContext == nullptr) return;
	const auto widthScale = (std::clamp)(
		animator_.Current().widthScale,
		0.0f,
		1.0f);
	const auto animatedWidth = (std::max)(1.0f, static_cast<float>(config_.width) * widthScale);
	const auto animatedLeft = (static_cast<float>(config_.width) - animatedWidth) / 2.0f;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto caret = GetCaretLogicalPosition();
	const auto textTop = TextTopDip();
	COMPOSITIONFORM compositionForm{};
	compositionForm.dwStyle = CFS_POINT;
	compositionForm.ptCurrentPos.x = DipToPixels(
		animatedLeft + horizontalPadding + caret.x,
		dpi_);
	compositionForm.ptCurrentPos.y = DipToPixels(
		textTop + caret.y - verticalOffset_ + config_.fontSize,
		dpi_);
	ImmSetCompositionWindow(inputContext, &compositionForm);
	ImmReleaseContext(hwnd_, inputContext);
}

void InputWindow::Render(bool caretOnly)
{
	if (hwnd_ == nullptr || FAILED(EnsureResources())) return;
	const auto animationFrame = animator_.Current();
	caretOnly = caretOnly
		&& animationFrame.state == PopupAnimationState::Visible;
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	const auto fullWidth = static_cast<float>(config_.width);
	const auto animatedWidth = (std::max)(
		1.0f,
		fullWidth * (std::clamp)(animationFrame.widthScale, 0.0f, 1.0f));
	const auto animatedLeft = (fullWidth - animatedWidth) / 2.0f;
	const auto animatedRight = animatedLeft + animatedWidth;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto windowHeight = WindowHeightDip();
	const auto verticalPadding = (std::min)(
		(std::max)(0.0f, config_.verticalPadding),
		windowHeight / 2.0f - 1.0f);
	const auto lineHeight = LineHeightDip();
	const auto textTop = TextTopDip();
	const auto textRect = D2D1::RectF(
		animatedLeft + horizontalPadding, textTop,
		animatedRight - horizontalPadding,
		(std::min)(windowHeight - verticalPadding, textTop + TextViewportHeightDip()));
	const auto caret = GetCaretLogicalPosition();
	const auto caretX = textRect.left + caret.x;
	const auto caretHeight = (std::min)(
		lineHeight,
		(std::max)(1.0f, config_.fontSize * 1.1f));
	const auto caretTop = textTop + caret.y - verticalOffset_
		+ (lineHeight - caretHeight) / 2.0f;
	RECT nextCaretDirty{
		DipToPixels(caretX, dpi_) - 2,
		DipToPixels(caretTop, dpi_) - 2,
		DipToPixels(caretX + config_.caretWidth, dpi_) + 2,
		DipToPixels(caretTop + caretHeight, dpi_) + 2,
	};
	nextCaretDirty.left = (std::clamp)(nextCaretDirty.left, 0L, static_cast<LONG>(width));
	nextCaretDirty.top = (std::clamp)(nextCaretDirty.top, 0L, static_cast<LONG>(height));
	nextCaretDirty.right = (std::clamp)(
		nextCaretDirty.right,
		nextCaretDirty.left,
		static_cast<LONG>(width));
	nextCaretDirty.bottom = (std::clamp)(
		nextCaretDirty.bottom,
		nextCaretDirty.top,
		static_cast<LONG>(height));
	const auto nextCaretDirtyValid = nextCaretDirty.right > nextCaretDirty.left
		&& nextCaretDirty.bottom > nextCaretDirty.top;
	const auto dirtyMatches = caretDirtyValid_ && nextCaretDirtyValid
		&& EqualRect(&caretDirtyRect_, &nextCaretDirty) != FALSE;
	caretOnly = caretOnly && dirtyMatches;
	if (caretOnly)
	{
		surface_->Clear(caretDirtyRect_);
	}

	RECT bindRect{ 0, 0, width, height };
	if (FAILED(renderTarget_->BindDC(surface_->DeviceContext(), &bindRect))) return;
	renderTarget_->BeginDraw();
	renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	// Make the layered window's per-pixel alpha behavior explicit; ClearType is
	// not suitable for a premultiplied transparent render target.
	renderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	if (caretOnly)
	{
		renderTarget_->PushAxisAlignedClip(
			D2D1::RectF(
				PixelsToDip(caretDirtyRect_.left, dpi_),
				PixelsToDip(caretDirtyRect_.top, dpi_),
				PixelsToDip(caretDirtyRect_.right, dpi_),
				PixelsToDip(caretDirtyRect_.bottom, dpi_)),
			D2D1_ANTIALIAS_MODE_ALIASED);
	}
	else
	{
		renderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	}
	const auto rounded = CreateInsetRoundedRect(
		animatedLeft,
		0.0f,
		animatedRight,
		WindowHeightDip(),
		config_.cornerRadius,
		config_.borderThickness);
	renderTarget_->FillRoundedRectangle(rounded, backgroundBrush_.Get());
	if (config_.borderThickness > 0.0f)
	{
		renderTarget_->DrawRoundedRectangle(
			rounded, borderBrush_.Get(), config_.borderThickness);
	}
	renderTarget_->PushAxisAlignedClip(textRect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	if (text_.empty())
	{
		const auto placeholderRect = D2D1::RectF(
			textRect.left, textTop, textRect.right, textTop + lineHeight);
		renderTarget_->DrawTextW(
			PlaceholderText, static_cast<UINT32>(std::size(PlaceholderText) - 1), textFormat_.Get(),
			placeholderRect, placeholderBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
	}
	else if (textLayout_)
	{
		renderTarget_->DrawTextLayout(
			D2D1::Point2F(textRect.left, textTop - verticalOffset_),
			textLayout_.Get(), textBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
	}
	if (caretVisible_)
	{
		renderTarget_->FillRectangle(
			D2D1::RectF(caretX, caretTop, caretX + config_.caretWidth, caretTop + caretHeight),
			caretBrush_.Get());
	}
	renderTarget_->PopAxisAlignedClip();
	if (caretOnly)
	{
		renderTarget_->PopAxisAlignedClip();
	}
	const auto endResult = renderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardResources(false);
		return;
	}
	if (FAILED(endResult))
	{
		caretDirtyValid_ = false;
		return;
	}

	caretDirtyRect_ = nextCaretDirty;
	caretDirtyValid_ = nextCaretDirtyValid;
	surface_->Present(
		hwnd_,
		width,
		height,
		caretOnly ? &caretDirtyRect_ : nullptr,
		static_cast<BYTE>(std::lround(
			(std::clamp)(animationFrame.opacity, 0.0f, 1.0f) * 255.0f)));
}

float InputWindow::LineHeightDip() const
{
	return (std::max)(1.0f, config_.fontSize * 1.25f);
}

float InputWindow::WindowHeightDip() const
{
	const auto singleLineHeight = (std::max)(
		static_cast<float>(config_.height),
		2.0f * (std::max)(0.0f, config_.verticalPadding) + LineHeightDip());
	const auto requestedHeight = singleLineHeight
		+ static_cast<float>((std::max)(1, lineCapacity_) - 1) * LineHeightDip();

	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (hwnd_ != nullptr
			? MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST)
			: nullptr);
	if (monitor == nullptr) return requestedHeight;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return requestedHeight;
	const auto workHeight = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;
	const auto placementMargin = config_.positionMode == 1 || config_.positionMode == 3
		? 0
		: DipToPixels(static_cast<float>(config_.bottomMargin), dpi_);
	const auto widthPixels = (std::max)(
		1,
		DipToPixels(static_cast<float>(config_.width), dpi_));
	const auto areaLimitedHeight = static_cast<LONG>((std::max)(
		int64_t{ 1 },
		MaxSurfacePixels / static_cast<int64_t>(widthPixels)));
	const auto maximumHeightPixels = (std::max)(1L,
		(std::min)({
			4096L,
			areaLimitedHeight,
			static_cast<LONG>(workHeight) - static_cast<LONG>(placementMargin),
		}));
	return (std::min)(requestedHeight, PixelsToDip(maximumHeightPixels, dpi_));
}

float InputWindow::TextWidthDip() const
{
	return (std::max)(
		1.0f,
		static_cast<float>(config_.width) - 2.0f * (std::max)(0.0f, config_.horizontalPadding));
}

float InputWindow::TextTopDip() const
{
	return (std::max)(
		(std::max)(0.0f, config_.verticalPadding),
		(WindowHeightDip()
			- static_cast<float>((std::max)(1, lineCapacity_)) * LineHeightDip()) / 2.0f);
}

float InputWindow::TextViewportHeightDip() const
{
	const auto requestedViewport = static_cast<float>((std::max)(1, lineCapacity_))
		* LineHeightDip();
	const auto availableViewport = (std::max)(
		1.0f,
		WindowHeightDip() - TextTopDip()
			- (std::max)(0.0f, config_.verticalPadding));
	return (std::min)(requestedViewport, availableViewport);
}

int InputWindow::PixelWidth() const
{
	return (std::max)(1, DipToPixels(static_cast<float>(config_.width), dpi_));
}

int InputWindow::PixelHeight() const
{
	return (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
}

bool InputWindow::HandleKeyDown(WPARAM wParam)
{
	if ((wParam == L'V' || wParam == L'v') && IsKeyDown(VK_CONTROL))
	{
		PasteFromClipboard();
		return true;
	}
	if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
	{
		Hide();
		return true;
	}
	if (MatchesHotkey(wParam, config_.submitVirtualKey, config_.submitModifiers))
	{
		Submit();
		return true;
	}
	if (MatchesHotkey(wParam, config_.backspaceVirtualKey, config_.backspaceModifiers))
	{
		DeleteBeforeCaret();
		return true;
	}
	if (GetCurrentHotkeyModifiers() == 0)
	{
		switch (wParam)
		{
		case VK_DELETE: DeleteAtCaret(); return true;
		case VK_LEFT: MoveCaretLeft(); return true;
		case VK_RIGHT: MoveCaretRight(); return true;
		case VK_HOME: MoveCaretToStart(); return true;
		case VK_END: MoveCaretToEnd(); return true;
		case VK_UP: NavigateHistory(-1); return true;
		case VK_DOWN: NavigateHistory(1); return true;
		default: break;
		}
	}
	return false;
}

LRESULT InputWindow::HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND: return 1;
	case WM_SETFOCUS:
		caretVisible_ = visible_;
		if (visible_)
		{
			SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
		}
		UpdateImeCompositionWindow();
		Render();
		return 0;
	case WM_KILLFOCUS:
		caretVisible_ = false;
		KillTimer(hwnd_, CaretTimerId);
		Render();
		return 0;
	case WM_TIMER:
		if (wParam == AnimationTimerId)
		{
			AdvanceAnimation();
			return 0;
		}
		if (wParam == CaretTimerId)
		{
			if (!visible_)
			{
				KillTimer(hwnd_, CaretTimerId);
				caretVisible_ = false;
				return 0;
			}
			caretVisible_ = !caretVisible_;
			Render(true);
			return 0;
		}
		break;
	case WM_KEYDOWN:
	case WM_SYSKEYDOWN:
		if (!visible_) return 0;
		if (HandleKeyDown(wParam)) return 0;
		break;
	case WM_CHAR:
		if (!visible_) return 0;
		if (wParam == L'\b' || wParam == L'\r' || wParam == L'\n' || wParam == L'\t') return 0;
		if (wParam >= 0x20) InsertCharacter(static_cast<wchar_t>(wParam));
		return 0;
	case WM_SYSCHAR:
		return 0;
	case WM_PASTE:
		if (visible_) PasteFromClipboard();
		return 0;
	case WM_LBUTTONDOWN:
		if (visible_)
		{
			SetFocus(hwnd_);
			SetCaretFromPoint(lParam);
		}
		return 0;
	case WM_IME_STARTCOMPOSITION:
		if (!visible_) return 0;
		UpdateImeCompositionWindow();
		break;
	case WM_IME_COMPOSITION:
		if (!visible_) return 0;
		UpdateImeCompositionWindow();
		if ((lParam & GCS_RESULTSTR) != 0)
		{
			const auto inputContext = ImmGetContext(hwnd_);
			if (inputContext != nullptr)
			{
				const auto byteCount = ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, nullptr, 0);
				if (byteCount > 0)
				{
					std::wstring result(static_cast<size_t>(byteCount) / sizeof(wchar_t), L'\0');
					ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, result.data(), byteCount);
					InsertText(result);
				}
				ImmReleaseContext(hwnd_, inputContext);
			}
			return 0;
		}
		break;
	case WM_DPICHANGED:
		ApplyDpiChange(
			static_cast<UINT>(LOWORD(wParam)),
			reinterpret_cast<const RECT*>(lParam));
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paint{};
		BeginPaint(hwnd_, &paint);
		Render();
		EndPaint(hwnd_, &paint);
		return 0;
	}
	case WM_SIZE:
		Render();
		return 0;
	case WM_CLOSE:
		Hide();
		return 0;
	case WM_DESTROY:
		KillTimer(hwnd_, CaretTimerId);
		KillTimer(hwnd_, AnimationTimerId);
		DiscardResources(true);
		hwnd_ = nullptr;
		visible_ = false;
		return 0;
	default:
		break;
	}
	return DefWindowProcW(window, message, wParam, lParam);
}


void InputWindow::RecordHistory(const std::wstring& value)
{
	if (!value.empty() && (historyEntries_.empty() || historyEntries_.back() != value))
	{
		historyEntries_.push_back(value);
		if (historyEntries_.size() > HistoryCapacity)
		{
			historyEntries_.erase(historyEntries_.begin());
		}
	}
	ResetHistoryNavigation();
}

void InputWindow::ResetHistoryNavigation() noexcept
{
	historyNavigationIndex_ = -1;
	historyDraft_.clear();
}

bool InputWindow::TryNavigateHistory(int direction, std::wstring& value)
{
	if (historyEntries_.empty()) return false;
	if (historyNavigationIndex_ < 0)
	{
		if (direction > 0) return false;
		historyDraft_ = value;
		historyNavigationIndex_ = static_cast<int>(historyEntries_.size()) - 1;
	}
	else
	{
		historyNavigationIndex_ += direction;
	}

	if (historyNavigationIndex_ < 0) historyNavigationIndex_ = 0;
	if (historyNavigationIndex_ >= static_cast<int>(historyEntries_.size()))
	{
		historyNavigationIndex_ = -1;
		value = historyDraft_;
		historyDraft_.clear();
	}
	else
	{
		value = historyEntries_[historyNavigationIndex_];
	}
	return true;
}
