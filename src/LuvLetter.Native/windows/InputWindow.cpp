#include "windows/InputWindow.h"

#include "configuration/NativeConfigurationSanitizer.h"
#include "rendering/SurfaceStyleDefaults.h"
#include "windows/InputModeBehavior.h"

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
	constexpr float WorkAreaMarginDip = 16.0f;
	constexpr wchar_t PlaceholderText[] = L"Enter command here";
	constexpr int AltModifier = 1;
	constexpr int ControlModifier = 2;
	constexpr int ShiftModifier = 4;
	constexpr int WindowsModifier = 8;

	struct InputAdornmentMetrics final
	{
		float indicatorDiameter;
		float indicatorGap;
		float tagWidth;
		float tagHeight;
		float tagGap;
		float tagCornerRadius;
		float tagBorderWidth;
	};

	InputAdornmentMetrics CalculateInputAdornmentMetrics(float fontSize) noexcept
	{
		const auto size = (std::max)(1.0f, fontSize);
		return InputAdornmentMetrics{
			size,
			size * (4.0f / 7.0f),
			size * 3.25f,
			size * 1.75f,
			size * (4.0f / 7.0f),
			size * (5.0f / 14.0f),
			(std::max)(0.5f, size / 28.0f),
		};
	}

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

	enum class CharacterClass
	{
		Whitespace,
		Word,
		Other,
	};

	CharacterClass ClassifyCharacter(const std::wstring& value, size_t index) noexcept
	{
		if (index >= value.size()) return CharacterClass::Other;
		WORD ctype1 = 0;
		WORD ctype3 = 0;
		const auto character = value[index];
		GetStringTypeW(CT_CTYPE1, &character, 1, &ctype1);
		GetStringTypeW(CT_CTYPE3, &character, 1, &ctype3);
		if ((ctype1 & (C1_SPACE | C1_BLANK)) != 0) return CharacterClass::Whitespace;
		if (character == L'_'
			|| (ctype1 & (C1_ALPHA | C1_DIGIT)) != 0
			|| (ctype3 & (C3_NONSPACING | C3_DIACRITIC | C3_VOWELMARK)) != 0)
		{
			return CharacterClass::Word;
		}
		return CharacterClass::Other;
	}

	D2D1_COLOR_F SystemColor(int colorIndex) noexcept
	{
		const auto color = GetSysColor(colorIndex);
		return D2D1::ColorF(
			static_cast<float>(GetRValue(color)) / 255.0f,
			static_cast<float>(GetGValue(color)) / 255.0f,
			static_cast<float>(GetBValue(color)) / 255.0f,
			1.0f);
	}

	D2D1_COLOR_F InputModeBorderColor(LuvLetterInputMode mode) noexcept
	{
		switch (mode)
		{
		case LuvLetterInputModeAsk: return D2D1::ColorF(0xF59E0B, 1.0f);
		case LuvLetterInputModeCommand: return D2D1::ColorF(0xA855F7, 1.0f);
		default: return D2D1::ColorF(0x22C55E, 1.0f);
		}
	}

	D2D1_COLOR_F InterpolateColor(
		const D2D1_COLOR_F& from,
		const D2D1_COLOR_F& to,
		float progress) noexcept
	{
		const auto amount = (std::clamp)(progress, 0.0f, 1.0f);
		return D2D1::ColorF(
			from.r + (to.r - from.r) * amount,
			from.g + (to.g - from.g) * amount,
			from.b + (to.b - from.b) * amount,
			from.a + (to.a - from.a) * amount);
	}

}

InputWindow::InputWindow(
	ID2D1Factory* d2dFactory,
	IDWriteFactory* dwriteFactory,
	std::function<void(const std::wstring&, int32_t)> submitted,
	std::function<void(const std::wstring&, int32_t, uint64_t)> changed,
	std::function<bool(int)> moveCandidateSelection,
	std::function<bool(int32_t)> activateCandidate,
	std::function<void()> hideCandidates)
	: config_(NativeConfigurationSanitizer::DefaultInputBox()),
	submitted_(std::move(submitted)),
	changed_(std::move(changed)),
	moveCandidateSelection_(std::move(moveCandidateSelection)),
	activateCandidate_(std::move(activateCandidate)),
	hideCandidates_(std::move(hideCandidates)),
	d2dFactory_(d2dFactory),
	dwriteFactory_(dwriteFactory),
	surface_(std::make_unique<LayeredWindowSurface>()),
	shadowWindow_(d2dFactory)
{
}

HRESULT InputWindow::Attach(HWND window)
{
	if (window == nullptr || hwnd_ != nullptr) return E_INVALIDARG;
	hwnd_ = window;
	dpi_ = QueryWindowDpi(hwnd_);
	(void)shadowWindow_.Attach(hwnd_);
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
		UpdateTypographyMetrics();
		result = dwriteFactory_->CreateTextFormat(
			SurfaceFontFamily, nullptr, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize, L"", textFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
		textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
		result = textFormat_->SetLineSpacing(
			DWRITE_LINE_SPACING_METHOD_UNIFORM,
			LineHeightDip(),
			textBaselineDip_);
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
	if (!statusTagTextFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			SurfaceFontFamily, nullptr, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize, L"",
			statusTagTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		result = statusTagTextFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
		if (FAILED(result)) return result;
		result = statusTagTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		if (FAILED(result)) return result;
		result = statusTagTextFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
		if (FAILED(result)) return result;
		result = statusTagTextFormat_->SetLineSpacing(
			DWRITE_LINE_SPACING_METHOD_UNIFORM, LineHeightDip(), textBaselineDip_);
		if (FAILED(result)) return result;
	}
	if (!focusIndicatorBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(0x22C55E, 1.0f), focusIndicatorBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!statusTagBorderBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			InputModeBorderColor(inputMode_), statusTagBorderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
		statusTagBorderBrush_->SetOpacity(0.90f);
	}
	if (!statusTagTextBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.textColor), statusTagTextBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!selectionBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			SystemColor(COLOR_HIGHLIGHT), selectionBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!selectionTextBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			SystemColor(COLOR_HIGHLIGHTTEXT), selectionTextBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!text_.empty() && !textLayout_)
	{
		result = dwriteFactory_->CreateTextLayout(
			text_.c_str(), static_cast<UINT32>(text_.size()), textFormat_.Get(),
			TextWidthDip(), MaxTextLayoutHeight, textLayout_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	else if (textLayout_)
	{
		// The focus indicator changes the leading reservation every animation
		// frame, so keep DirectWrite wrapping aligned with the rendered geometry.
		result = textLayout_->SetMaxWidth(TextWidthDip());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void InputWindow::DiscardResources(bool discardSurface)
{
	caretDirtyValid_ = false;
	textLayout_.Reset();
	statusTagTextBrush_.Reset();
	statusTagBorderBrush_.Reset();
	selectionTextBrush_.Reset();
	selectionBrush_.Reset();
	focusIndicatorBrush_.Reset();
	caretBrush_.Reset();
	placeholderBrush_.Reset();
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	statusTagTextFormat_.Reset();
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
	}
	else
	{
		targetMonitor_ = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
	}
	UpdateResponsiveHeight();
	UpdateWindowPosition();
	UpdateWindowShape();
	UpdateImeCompositionWindow();
	if (visible_) Render();
}

void InputWindow::Show(HMONITOR targetMonitor, HWND previousForegroundWindow)
{
	if (hwnd_ == nullptr) return;
	SynchronizeAnimation();
	const auto wasPresenting = animator_.Current().ShouldPresent();
	if (!wasPresenting)
	{
		focusIndicatorAnimator_.Reset(false);
	}
	previousForegroundHwnd_ = previousForegroundWindow;
	targetMonitor_ = targetMonitor;
	EnableWindow(hwnd_, TRUE);
	Reset();
	KillTimer(hwnd_, CaretTimerId);
	caretVisible_ = false;
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
	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
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
	if (hideCandidates_) hideCandidates_();
	if (!visible_ && !animator_.TargetVisible())
	{
		if (IsWindowEnabled(hwnd_)) EnableWindow(hwnd_, FALSE);
		shadowWindow_.Hide();
		return;
	}
	SynchronizeAnimation();
	visible_ = false;
	KillTimer(hwnd_, CaretTimerId);
	caretVisible_ = false;
	mouseSelecting_ = false;
	if (GetCapture() == hwnd_) ReleaseCapture();
	animator_.Hide();
	focusIndicatorAnimator_.Hide();
	// Let the visible Tag transition finish with the popup, but do not delay
	// dismissal by playing transitions that have not started yet.
	pendingStatusTagModes_.clear();
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
	if (hideCandidates_) hideCandidates_();
	visible_ = false;
	caretVisible_ = false;
	KillTimer(hwnd_, CaretTimerId);
	KillTimer(hwnd_, AnimationTimerId);
	mouseSelecting_ = false;
	if (GetCapture() == hwnd_) ReleaseCapture();
	animator_.Reset(false);
	focusIndicatorAnimator_.Reset(false);
	statusTagAnimator_.Reset(true);
	animationTimestamp_ = 0;
	caretDirtyValid_ = false;
	EnableWindow(hwnd_, FALSE);
	ShowWindow(hwnd_, SW_HIDE);
	shadowWindow_.Hide();
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
	if (animationTimestamp_ != 0)
	{
		const auto elapsed = now >= animationTimestamp_
			? static_cast<double>(now - animationTimestamp_)
			: 0.0;
		if (animator_.Current().IsAnimating())
		{
			animator_.Advance(elapsed);
		}
		if (focusIndicatorAnimator_.Current().IsAnimating())
		{
			focusIndicatorAnimator_.Advance(elapsed);
		}
		if (statusTagAnimator_.Current().IsAnimating())
		{
			statusTagAnimator_.Advance(elapsed);
			if (!statusTagAnimator_.Current().IsAnimating())
			{
				statusTagFromMode_ = statusTagToMode_;
				if (!pendingStatusTagModes_.empty())
				{
					statusTagToMode_ = pendingStatusTagModes_.front();
					pendingStatusTagModes_.pop_front();
					statusTagAnimator_.Reset(false);
					statusTagAnimator_.Show();
				}
			}
		}
	}
	animationTimestamp_ = now;
}

void InputWindow::AdvanceAnimation()
{
	if (hwnd_ == nullptr) return;
	SynchronizeAnimation();
	const auto frame = animator_.Current();
	const auto indicatorFrame = focusIndicatorAnimator_.Current();
	const auto statusTagFrame = statusTagAnimator_.Current();
	caretDirtyValid_ = false;
	UpdateResponsiveHeight();
	EnsureCaretVisible();
	UpdateWindowPosition();
	UpdateImeCompositionWindow();
	Render();
	if (frame.IsAnimating() || indicatorFrame.IsAnimating() || statusTagFrame.IsAnimating()) return;
	KillTimer(hwnd_, AnimationTimerId);
	if (!frame.ShouldPresent())
	{
		CompleteHide();
		return;
	}
	animationTimestamp_ = 0;
}

void InputWindow::CompleteHide()
{
	if (hwnd_ == nullptr) return;
	KillTimer(hwnd_, AnimationTimerId);
	ShowWindow(hwnd_, SW_HIDE);
	shadowWindow_.Hide();
	focusIndicatorAnimator_.Reset(false);
	statusTagAnimator_.Reset(true);
	animationTimestamp_ = 0;
}

void InputWindow::SetFocusIndicatorTarget(bool focused) noexcept
{
	if (hwnd_ == nullptr || focusIndicatorAnimator_.TargetVisible() == focused) return;
	SynchronizeAnimation();
	if (focused)
	{
		focusIndicatorAnimator_.Show();
	}
	else
	{
		focusIndicatorAnimator_.Hide();
	}
	caretDirtyValid_ = false;
	animationTimestamp_ = GetTickCount64();
	if (focusIndicatorAnimator_.Current().IsAnimating()
		&& SetTimer(hwnd_, AnimationTimerId, AnimationFrameMs, nullptr) == 0)
	{
		focusIndicatorAnimator_.Reset(focused);
		animationTimestamp_ = 0;
	}
}

void InputWindow::UpdateWindowPosition(bool applyAnimation) const
{
	if (hwnd_ == nullptr) return;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
	if (!GetMonitorInfoW(monitor, &monitorInfo))
	{
		monitor = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
		if (!GetMonitorInfoW(monitor, &monitorInfo)) return;
	}
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
	x = (std::clamp)(
		x,
		monitorInfo.rcWork.left,
		(std::max)(monitorInfo.rcWork.left, monitorInfo.rcWork.right - width));
	y = (std::clamp)(
		y,
		monitorInfo.rcWork.top,
		(std::max)(monitorInfo.rcWork.top, monitorInfo.rcWork.bottom - height));
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
	selectionAnchor_ = 0;
	mouseSelecting_ = false;
	spaceModeSwitchKeyDown_ = false;
	inputMode_ = LuvLetterInputModeGeneral;
	statusTagFromMode_ = inputMode_;
	statusTagToMode_ = inputMode_;
	pendingStatusTagModes_.clear();
	statusTagAnimator_.Reset(true);
	lineCapacity_ = 1;
	verticalOffset_ = 0.0f;
	ResetHistoryNavigation();
	PublishInputChanged();
}

void InputWindow::PublishInputChanged()
{
	SetInputMode(ArkheideSystem::ResolveInputModeForText(inputMode_, text_));
	++revision_;
	if (revision_ == 0)
	{
		++revision_;
	}
	if (changed_)
	{
		changed_(text_, static_cast<int32_t>(inputMode_), revision_);
	}
}

void InputWindow::Submit()
{
	if (!text_.empty() && submitted_)
	{
		submitted_(text_, static_cast<int32_t>(inputMode_));
	}
	const auto hadText = !text_.empty();
	RecordHistory(text_);
	text_.clear();
	textLayout_.Reset();
	caretIndex_ = 0;
	selectionAnchor_ = 0;
	verticalOffset_ = 0.0f;
	if (hadText) PublishInputChanged();
	Invalidate();
}

void InputWindow::CycleInputMode()
{
	LuvLetterInputMode nextMode;
	switch (inputMode_)
	{
	case LuvLetterInputModeGeneral:
		nextMode = LuvLetterInputModeAsk;
		break;
	case LuvLetterInputModeAsk:
		nextMode = LuvLetterInputModeCommand;
		break;
	default:
		nextMode = LuvLetterInputModeGeneral;
		break;
	}
	SetInputMode(nextMode);
	PublishInputChanged();
	Invalidate();
}

void InputWindow::SetInputMode(LuvLetterInputMode mode)
{
	if (inputMode_ == mode) return;

	SynchronizeAnimation();
	inputMode_ = mode;
	if (statusTagAnimator_.Current().IsAnimating())
	{
		pendingStatusTagModes_.push_back(inputMode_);
	}
	else
	{
		statusTagFromMode_ = statusTagToMode_;
		statusTagToMode_ = inputMode_;
		statusTagAnimator_.Reset(false);
		statusTagAnimator_.Show();
	}
	caretDirtyValid_ = false;
	animationTimestamp_ = GetTickCount64();
	if (statusTagAnimator_.Current().IsAnimating()
		&& SetTimer(hwnd_, AnimationTimerId, AnimationFrameMs, nullptr) == 0)
	{
		pendingStatusTagModes_.clear();
		statusTagFromMode_ = inputMode_;
		statusTagToMode_ = inputMode_;
		statusTagAnimator_.Reset(true);
		if (!animator_.Current().IsAnimating()
			&& !focusIndicatorAnimator_.Current().IsAnimating())
		{
			animationTimestamp_ = 0;
		}
	}
}

void InputWindow::InsertText(const std::wstring& value)
{
	if (value.empty()) return;
	const auto selectedCount = SelectionEnd() - SelectionStart();
	const auto retainedCount = text_.size() - selectedCount;
	if (retainedCount >= MaxInputCharacters) return;
	size_t valueStart = 0;
	if (retainedCount == 0)
	{
		while (valueStart < value.size() && value[valueStart] == L' ')
		{
			++valueStart;
		}
	}
	if (valueStart == value.size()) return;
	const auto count = (std::min)(
		value.size() - valueStart,
		MaxInputCharacters - retainedCount);
	auto safeCount = count;
	if (valueStart + safeCount < value.size() && safeCount > 0
		&& IsHighSurrogate(value[valueStart + safeCount - 1])
		&& IsLowSurrogate(value[valueStart + safeCount]))
	{
		--safeCount;
	}
	if (safeCount == 0) return;
	EraseSelection();
	text_.insert(caretIndex_, value.data() + valueStart, safeCount);
	textLayout_.Reset();
	caretIndex_ += safeCount;
	selectionAnchor_ = caretIndex_;
	ResetHistoryNavigation();
	PublishInputChanged();
	Invalidate();
}

void InputWindow::InsertCharacter(wchar_t value)
{
	const auto selectedCount = SelectionEnd() - SelectionStart();
	const auto retainedCount = text_.size() - selectedCount;
	if (retainedCount >= MaxInputCharacters) return;
	if (retainedCount == 0 && value == L' ') return;
	// WM_CHAR delivers a supplementary character as two UTF-16 code units. Do not
	// admit the high surrogate into the final slot, where its low surrogate could
	// no longer be appended. Likewise, ignore an unmatched low surrogate.
	if (IsHighSurrogate(value) && MaxInputCharacters - retainedCount < 2) return;
	const auto insertionIndex = SelectionStart();
	if (IsLowSurrogate(value)
		&& (insertionIndex == 0 || !IsHighSurrogate(text_[insertionIndex - 1]))) return;
	EraseSelection();
	text_.insert(caretIndex_, 1, value);
	textLayout_.Reset();
	++caretIndex_;
	selectionAnchor_ = caretIndex_;
	ResetHistoryNavigation();
	PublishInputChanged();
	Invalidate();
}

void InputWindow::EraseRange(size_t start, size_t end)
{
	start = (std::min)(start, text_.size());
	end = (std::clamp)(end, start, text_.size());
	if (start == end) return;
	text_.erase(start, end - start);
	textLayout_.Reset();
	caretIndex_ = start;
	selectionAnchor_ = start;
	ResetHistoryNavigation();
}

bool InputWindow::EraseSelection()
{
	if (!HasSelection()) return false;
	EraseRange(SelectionStart(), SelectionEnd());
	return true;
}

void InputWindow::DeleteBeforeCaret(bool byWord)
{
	const auto previousSize = text_.size();
	if (!EraseSelection())
	{
		if (caretIndex_ == 0 || text_.empty()) return;
		const auto previous = byWord
			? PreviousWordBoundary(caretIndex_)
			: PreviousUtf16Boundary(text_, caretIndex_);
		EraseRange(previous, caretIndex_);
	}
	if (text_.size() != previousSize) PublishInputChanged();
	Invalidate();
}

void InputWindow::DeleteAtCaret(bool byWord)
{
	const auto previousSize = text_.size();
	if (!EraseSelection())
	{
		if (caretIndex_ >= text_.size()) return;
		const auto next = byWord
			? NextWordBoundary(caretIndex_)
			: NextUtf16Boundary(text_, caretIndex_);
		EraseRange(caretIndex_, next);
	}
	if (text_.size() != previousSize) PublishInputChanged();
	Invalidate();
}

bool InputWindow::HasSelection() const noexcept
{
	return caretIndex_ != selectionAnchor_;
}

size_t InputWindow::SelectionStart() const noexcept
{
	return (std::min)(caretIndex_, selectionAnchor_);
}

size_t InputWindow::SelectionEnd() const noexcept
{
	return (std::max)(caretIndex_, selectionAnchor_);
}

void InputWindow::CollapseSelection() noexcept
{
	selectionAnchor_ = caretIndex_;
}

void InputWindow::MoveCaretTo(size_t index, bool extendSelection)
{
	index = (std::min)(index, text_.size());
	if (index > 0 && index < text_.size()
		&& IsHighSurrogate(text_[index - 1]) && IsLowSurrogate(text_[index]))
	{
		index = NextUtf16Boundary(text_, index - 1);
	}
	if (caretIndex_ == index && (extendSelection || !HasSelection())) return;
	caretIndex_ = index;
	if (!extendSelection) CollapseSelection();
	Invalidate();
}

void InputWindow::MoveCaretLeft(bool extendSelection, bool byWord)
{
	if (!extendSelection && HasSelection())
	{
		MoveCaretTo(SelectionStart(), false);
		return;
	}
	const auto target = byWord
		? PreviousWordBoundary(caretIndex_)
		: PreviousUtf16Boundary(text_, caretIndex_);
	MoveCaretTo(target, extendSelection);
}

void InputWindow::MoveCaretRight(bool extendSelection, bool byWord)
{
	if (!extendSelection && HasSelection())
	{
		MoveCaretTo(SelectionEnd(), false);
		return;
	}
	const auto target = byWord
		? NextWordBoundary(caretIndex_)
		: NextUtf16Boundary(text_, caretIndex_);
	MoveCaretTo(target, extendSelection);
}

void InputWindow::MoveCaretToStart(bool extendSelection)
{
	MoveCaretTo(0, extendSelection);
}

void InputWindow::MoveCaretToEnd(bool extendSelection)
{
	MoveCaretTo(text_.size(), extendSelection);
}

void InputWindow::SelectAll()
{
	if (text_.empty()) return;
	selectionAnchor_ = 0;
	caretIndex_ = text_.size();
	Invalidate();
}

size_t InputWindow::PreviousWordBoundary(size_t index) const noexcept
{
	index = (std::min)(index, text_.size());
	while (index > 0)
	{
		const auto previous = PreviousUtf16Boundary(text_, index);
		if (ClassifyCharacter(text_, previous) == CharacterClass::Word) break;
		index = previous;
	}
	while (index > 0)
	{
		const auto previous = PreviousUtf16Boundary(text_, index);
		if (ClassifyCharacter(text_, previous) != CharacterClass::Word) break;
		index = previous;
	}
	return index;
}

size_t InputWindow::NextWordBoundary(size_t index) const noexcept
{
	index = (std::min)(index, text_.size());
	if (index >= text_.size()) return text_.size();
	if (ClassifyCharacter(text_, index) == CharacterClass::Word)
	{
		while (index < text_.size() && ClassifyCharacter(text_, index) == CharacterClass::Word)
		{
			index = NextUtf16Boundary(text_, index);
		}
	}
	while (index < text_.size() && ClassifyCharacter(text_, index) != CharacterClass::Word)
	{
		index = NextUtf16Boundary(text_, index);
	}
	return index;
}

void InputWindow::NavigateHistory(int direction)
{
	const auto previousText = text_;
	if (!TryNavigateHistory(direction, text_)) return;
	textLayout_.Reset();
	caretIndex_ = text_.size();
	selectionAnchor_ = caretIndex_;
	if (text_ != previousText) PublishInputChanged();
	Invalidate();
}

bool InputWindow::CopySelectionToClipboard() const
{
	if (hwnd_ == nullptr || !HasSelection()) return false;
	const auto start = SelectionStart();
	const auto characterCount = SelectionEnd() - start;
	const auto byteCount = (characterCount + 1) * sizeof(wchar_t);
	const auto handle = GlobalAlloc(GMEM_MOVEABLE, byteCount);
	if (handle == nullptr) return false;
	const auto data = static_cast<wchar_t*>(GlobalLock(handle));
	if (data == nullptr)
	{
		GlobalFree(handle);
		return false;
	}
	CopyMemory(data, text_.data() + start, characterCount * sizeof(wchar_t));
	data[characterCount] = L'\0';
	GlobalUnlock(handle);

	if (!OpenClipboard(hwnd_))
	{
		GlobalFree(handle);
		return false;
	}
	const auto emptied = EmptyClipboard() != FALSE;
	const auto stored = emptied && SetClipboardData(CF_UNICODETEXT, handle) != nullptr;
	CloseClipboard();
	if (!stored) GlobalFree(handle);
	return stored;
}

void InputWindow::CutSelectionToClipboard()
{
	if (!CopySelectionToClipboard()) return;
	if (EraseSelection()) PublishInputChanged();
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

void InputWindow::SetCaretFromPoint(LPARAM lParam, bool extendSelection)
{
	if (text_.empty() || FAILED(EnsureResources()))
	{
		MoveCaretTo(text_.size(), extendSelection);
		return;
	}
	BOOL trailing = FALSE;
	BOOL inside = FALSE;
	DWRITE_HIT_TEST_METRICS metrics{};
	const auto widthScale = (std::clamp)(
		animator_.Current().widthScale,
		0.0f,
		1.0f);
	const auto fullWidth = PixelsToDip(PixelWidth(), dpi_);
	const auto animatedWidth = (std::max)(1.0f, fullWidth * widthScale);
	const auto animatedLeft = (fullWidth - animatedWidth) / 2.0f;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto leadingReservation = (std::min)(
		LeadingReservationDip(),
		(std::max)(0.0f, animatedWidth - 2.0f * horizontalPadding - 1.0f));
	const auto x = PixelsToDip(GET_X_LPARAM(lParam), dpi_)
		- animatedLeft
		- horizontalPadding
		- leadingReservation;
	const auto textTop = TextTopDip();
	const auto y = PixelsToDip(GET_Y_LPARAM(lParam), dpi_) - textTop + verticalOffset_;
	if (SUCCEEDED(textLayout_->HitTestPoint(x, y, &trailing, &inside, &metrics)))
	{
		auto target = (std::min)(
			static_cast<size_t>(metrics.textPosition + (trailing ? metrics.length : 0)),
			text_.size());
		if (target > 0 && target < text_.size()
			&& IsHighSurrogate(text_[target - 1]) && IsLowSurrogate(text_[target]))
		{
			target = trailing ? target + 1 : target - 1;
		}
		MoveCaretTo(target, extendSelection);
	}
}

void InputWindow::Invalidate()
{
	RefreshCaretState(true);
	UpdateResponsiveHeight();
	EnsureCaretVisible();
	if (hwnd_ != nullptr)
	{
		UpdateImeCompositionWindow();
		Render();
	}
}

bool InputWindow::HasKeyboardFocus() const noexcept
{
	return hwnd_ != nullptr
		&& visible_
		&& GetForegroundWindow() == hwnd_
		&& GetFocus() == hwnd_;
}

void InputWindow::RefreshFocusVisuals()
{
	RefreshCaretState(true);
	UpdateImeCompositionWindow();
	Render();
}

bool InputWindow::RefreshCaretState(bool restartBlink, bool forceInactive) noexcept
{
	const auto focused = !forceInactive && HasKeyboardFocus();
	SetFocusIndicatorTarget(focused);
	if (!focused)
	{
		if (hwnd_ != nullptr) KillTimer(hwnd_, CaretTimerId);
		caretVisible_ = false;
		return false;
	}

	if (restartBlink)
	{
		caretVisible_ = true;
		SetTimer(hwnd_, CaretTimerId, CaretBlinkMs, nullptr);
	}
	return true;
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
	const auto fullWidth = PixelsToDip(PixelWidth(), dpi_);
	const auto animatedWidth = (std::max)(1.0f, fullWidth * widthScale);
	const auto animatedLeft = (fullWidth - animatedWidth) / 2.0f;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto leadingReservation = (std::min)(
		LeadingReservationDip(),
		(std::max)(0.0f, animatedWidth - 2.0f * horizontalPadding - 1.0f));
	const auto caret = GetCaretLogicalPosition();
	const auto textTop = TextTopDip();
	COMPOSITIONFORM compositionForm{};
	compositionForm.dwStyle = CFS_POINT;
	compositionForm.ptCurrentPos.x = DipToPixels(
		animatedLeft + horizontalPadding + leadingReservation + caret.x,
		dpi_);
	compositionForm.ptCurrentPos.y = DipToPixels(
		textTop + caret.y - verticalOffset_ + textBaselineDip_,
		dpi_);
	ImmSetCompositionWindow(inputContext, &compositionForm);
	ImmReleaseContext(hwnd_, inputContext);
}

void InputWindow::Render(bool caretOnly)
{
	if (hwnd_ == nullptr || FAILED(EnsureResources())) return;
	const auto animationFrame = animator_.Current();
	const auto focusIndicatorFrame = focusIndicatorAnimator_.Current();
	const auto statusTagFrame = statusTagAnimator_.Current();
	caretOnly = caretOnly
		&& animationFrame.state == PopupAnimationState::Visible
		&& !focusIndicatorFrame.IsAnimating()
		&& !statusTagFrame.IsAnimating();
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	const auto fullWidth = PixelsToDip(width, dpi_);
	const auto animatedWidth = (std::max)(
		1.0f,
		fullWidth * (std::clamp)(animationFrame.widthScale, 0.0f, 1.0f));
	const auto animatedLeft = (fullWidth - animatedWidth) / 2.0f;
	const auto animatedRight = animatedLeft + animatedWidth;
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		(std::max)(0.0f, animatedWidth / 2.0f - 1.0f));
	const auto indicatorReservation = (std::min)(
		FocusIndicatorReservationDip(),
		(std::max)(0.0f, animatedWidth - 2.0f * horizontalPadding - 1.0f));
	const auto leadingReservation = (std::min)(
		LeadingReservationDip(),
		(std::max)(0.0f, animatedWidth - 2.0f * horizontalPadding - 1.0f));
	const auto windowHeight = WindowHeightDip();
	const auto adornmentMetrics = CalculateInputAdornmentMetrics(config_.fontSize);
	const auto verticalPadding = (std::min)(
		(std::max)(0.0f, config_.verticalPadding),
		windowHeight / 2.0f - 1.0f);
	const auto lineHeight = LineHeightDip();
	const auto textTop = TextTopDip();
	const auto textRect = D2D1::RectF(
		animatedLeft + horizontalPadding + leadingReservation, textTop,
		animatedRight - horizontalPadding,
		(std::min)(windowHeight - verticalPadding, textTop + TextViewportHeightDip()));
	const auto caret = GetCaretLogicalPosition();
	const auto caretX = textRect.left + caret.x;
	const auto caretHeight = (std::min)(lineHeight, caretHeightDip_);
	const auto caretTop = textTop + caret.y - verticalOffset_
		+ caretTopOffsetDip_;
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
	if (focusIndicatorFrame.motionProgress > 0.0)
	{
		const auto radius = adornmentMetrics.indicatorDiameter / 2.0f;
		const auto hiddenCenterX = animatedLeft - radius;
		const auto focusedCenterX = animatedLeft + horizontalPadding + radius;
		const auto centerX = hiddenCenterX
			+ (focusedCenterX - hiddenCenterX) * FocusIndicatorProgress();
		renderTarget_->PushAxisAlignedClip(
			D2D1::RectF(animatedLeft, 0.0f, animatedRight, windowHeight),
			D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
		renderTarget_->FillEllipse(
			D2D1::Ellipse(
				D2D1::Point2F(centerX, windowHeight / 2.0f),
				radius,
				radius),
			focusIndicatorBrush_.Get());
		renderTarget_->PopAxisAlignedClip();
	}
	const auto tagLeft = animatedLeft + horizontalPadding + indicatorReservation;
	const auto tagRight = (std::min)(
		tagLeft + adornmentMetrics.tagWidth,
		animatedRight - horizontalPadding);
	if (tagRight - tagLeft >= 8.0f)
	{
		const auto tagHeight = (std::min)(
			adornmentMetrics.tagHeight,
			(std::max)(1.0f, windowHeight - 2.0f));
		const auto tagTop = (windowHeight - tagHeight) / 2.0f;
		const auto tagRect = D2D1::RoundedRect(
			D2D1::RectF(tagLeft, tagTop, tagRight, tagTop + tagHeight),
			adornmentMetrics.tagCornerRadius,
			adornmentMetrics.tagCornerRadius);
		renderTarget_->PushAxisAlignedClip(
			D2D1::RectF(animatedLeft, 0.0f, animatedRight, windowHeight),
			D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
		const auto tagProgress = statusTagFrame.IsAnimating()
			? static_cast<float>((std::clamp)(statusTagFrame.motionProgress, 0.0, 1.0))
			: 1.0f;
		const auto borderFrom = statusTagFrame.IsAnimating()
			? InputModeBorderColor(statusTagFromMode_)
			: InputModeBorderColor(statusTagToMode_);
		statusTagBorderBrush_->SetColor(InterpolateColor(
			borderFrom,
			InputModeBorderColor(statusTagToMode_),
			tagProgress));
		renderTarget_->DrawRoundedRectangle(
			tagRect,
			statusTagBorderBrush_.Get(),
			adornmentMetrics.tagBorderWidth);
		renderTarget_->PushAxisAlignedClip(
			tagRect.rect,
			D2D1_ANTIALIAS_MODE_ALIASED);
		if (statusTagFrame.IsAnimating())
		{
			const auto travel = tagRect.rect.bottom - tagRect.rect.top;
			const auto outgoingRect = tagRect.rect;
			const auto incomingRect = tagRect.rect;
			renderTarget_->DrawTextW(
				InputModeLabel(statusTagFromMode_),
				3,
				statusTagTextFormat_.Get(),
				D2D1::RectF(
					outgoingRect.left,
					outgoingRect.top - travel * tagProgress,
					outgoingRect.right,
					outgoingRect.bottom - travel * tagProgress),
				statusTagTextBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
			renderTarget_->DrawTextW(
				InputModeLabel(statusTagToMode_),
				3,
				statusTagTextFormat_.Get(),
				D2D1::RectF(
					incomingRect.left,
					incomingRect.top + travel * (1.0f - tagProgress),
					incomingRect.right,
					incomingRect.bottom + travel * (1.0f - tagProgress)),
				statusTagTextBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}
		else
		{
			renderTarget_->DrawTextW(
				InputModeLabel(statusTagToMode_),
				3,
				statusTagTextFormat_.Get(),
				tagRect.rect,
				statusTagTextBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}
		renderTarget_->PopAxisAlignedClip();
		renderTarget_->PopAxisAlignedClip();
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
		const auto layoutOrigin = D2D1::Point2F(textRect.left, textTop - verticalOffset_);
		const auto fullRange = DWRITE_TEXT_RANGE{
			0,
			static_cast<UINT32>(text_.size()),
		};
		textLayout_->SetDrawingEffect(nullptr, fullRange);
		if (HasSelection())
		{
			const auto selectionRange = DWRITE_TEXT_RANGE{
				static_cast<UINT32>(SelectionStart()),
				static_cast<UINT32>(SelectionEnd() - SelectionStart()),
			};
			UINT32 metricCount = 0;
			const auto countResult = textLayout_->HitTestTextRange(
				selectionRange.startPosition,
				selectionRange.length,
				layoutOrigin.x,
				layoutOrigin.y,
				nullptr,
				0,
				&metricCount);
			if ((SUCCEEDED(countResult) || countResult == E_NOT_SUFFICIENT_BUFFER)
				&& metricCount > 0)
			{
				std::vector<DWRITE_HIT_TEST_METRICS> metrics(metricCount);
				if (SUCCEEDED(textLayout_->HitTestTextRange(
					selectionRange.startPosition,
					selectionRange.length,
					layoutOrigin.x,
					layoutOrigin.y,
					metrics.data(),
					metricCount,
					&metricCount)))
				{
					for (UINT32 index = 0; index < metricCount; ++index)
					{
						const auto& metric = metrics[index];
						renderTarget_->FillRectangle(
							D2D1::RectF(
								metric.left,
								metric.top,
								metric.left + metric.width,
								metric.top + metric.height),
							selectionBrush_.Get());
					}
				}
			}
			textLayout_->SetDrawingEffect(selectionTextBrush_.Get(), selectionRange);
		}
		renderTarget_->DrawTextLayout(
			layoutOrigin,
			textLayout_.Get(), textBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
	}
	if (caretVisible_ && HasKeyboardFocus())
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
	const auto popupOpacity = static_cast<float>((std::clamp)(
		animationFrame.opacity,
		0.0f,
		1.0f));
	const auto presented = surface_->Present(
		hwnd_,
		width,
		height,
		caretOnly ? &caretDirtyRect_ : nullptr,
		static_cast<BYTE>(std::lround(popupOpacity * 255.0f)));
	if (presented)
	{
		(void)shadowWindow_.Update(
			dpi_,
			{ LuvLetterNative::SurfaceShadowShape{
				D2D1::RectF(animatedLeft, 0.0f, animatedRight, windowHeight),
				config_.cornerRadius,
				popupOpacity } });
	}
}

float InputWindow::LineHeightDip() const
{
	return SurfaceLineHeightDip;
}

void InputWindow::UpdateTypographyMetrics() noexcept
{
	textBaselineDip_ = config_.fontSize;
	caretHeightDip_ = (std::min)(LineHeightDip(), (std::max)(1.0f, config_.fontSize));
	caretTopOffsetDip_ = (LineHeightDip() - caretHeightDip_) / 2.0f;
	if (!dwriteFactory_) return;

	Microsoft::WRL::ComPtr<IDWriteFontCollection> collection;
	if (FAILED(dwriteFactory_->GetSystemFontCollection(collection.GetAddressOf()))) return;
	UINT32 familyIndex = 0;
	BOOL familyExists = FALSE;
	if (FAILED(collection->FindFamilyName(
		SurfaceFontFamily, &familyIndex, &familyExists)) || !familyExists) return;
	Microsoft::WRL::ComPtr<IDWriteFontFamily> family;
	if (FAILED(collection->GetFontFamily(familyIndex, family.GetAddressOf()))) return;
	Microsoft::WRL::ComPtr<IDWriteFont> font;
	if (FAILED(family->GetFirstMatchingFont(
		DWRITE_FONT_WEIGHT_REGULAR,
		DWRITE_FONT_STRETCH_NORMAL,
		DWRITE_FONT_STYLE_NORMAL,
		font.GetAddressOf()))) return;

	DWRITE_FONT_METRICS metrics{};
	font->GetMetrics(&metrics);
	if (metrics.designUnitsPerEm == 0 || metrics.ascent == 0) return;
	const auto scale = config_.fontSize / static_cast<float>(metrics.designUnitsPerEm);
	const auto naturalHeight = static_cast<float>(metrics.ascent + metrics.descent) * scale;
	caretHeightDip_ = (std::clamp)(naturalHeight, 1.0f, LineHeightDip());
	caretTopOffsetDip_ = (LineHeightDip() - caretHeightDip_) / 2.0f;
	textBaselineDip_ = caretTopOffsetDip_ + static_cast<float>(metrics.ascent) * scale;
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
	const auto widthPixels = PixelWidth();
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
		PixelsToDip(PixelWidth(), dpi_)
			- 2.0f * (std::max)(0.0f, config_.horizontalPadding)
			- LeadingReservationDip());
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

float InputWindow::FocusIndicatorProgress() const noexcept
{
	return static_cast<float>((std::clamp)(
		focusIndicatorAnimator_.Current().motionProgress,
		0.0,
		1.0));
}

float InputWindow::FocusIndicatorReservationDip() const noexcept
{
	const auto metrics = CalculateInputAdornmentMetrics(config_.fontSize);
	return (metrics.indicatorDiameter + metrics.indicatorGap)
		* FocusIndicatorProgress();
}

float InputWindow::StatusTagReservationDip() const noexcept
{
	const auto metrics = CalculateInputAdornmentMetrics(config_.fontSize);
	return metrics.tagWidth + metrics.tagGap;
}

float InputWindow::LeadingReservationDip() const noexcept
{
	return FocusIndicatorReservationDip() + StatusTagReservationDip();
}

const wchar_t* InputWindow::InputModeLabel(LuvLetterInputMode mode) const noexcept
{
	switch (mode)
	{
	case LuvLetterInputModeAsk: return L"Ask";
	case LuvLetterInputModeCommand: return L"Cmd";
	default: return L"Gen";
	}
}

int InputWindow::PixelWidth() const
{
	const auto requestedWidth = (std::max)(
		1,
		DipToPixels(static_cast<float>(config_.width), dpi_));
	auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (hwnd_ != nullptr
			? MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST)
			: nullptr);
	if (monitor == nullptr) return requestedWidth;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (!GetMonitorInfoW(monitor, &monitorInfo))
	{
		if (hwnd_ == nullptr) return requestedWidth;
		monitor = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
		if (!GetMonitorInfoW(monitor, &monitorInfo)) return requestedWidth;
	}
	const auto workWidth = (std::max)(
		1L,
		monitorInfo.rcWork.right - monitorInfo.rcWork.left);
	const auto margin = (std::min)(
		workWidth / 2,
		static_cast<LONG>(DipToPixels(WorkAreaMarginDip, dpi_)));
	return (std::min)(
		requestedWidth,
		static_cast<int>((std::max)(1L, workWidth - 2 * margin)));
}

int InputWindow::PixelHeight() const
{
	return (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
}

bool InputWindow::HandleKeyDown(WPARAM wParam, LPARAM keyData)
{
	const auto modifiers = GetCurrentHotkeyModifiers();
	const auto controlDown = (modifiers & ControlModifier) != 0;
	const auto shiftDown = (modifiers & ShiftModifier) != 0;
	const auto hasSystemModifier = (modifiers & (AltModifier | WindowsModifier)) != 0;
	if (wParam == VK_SPACE && modifiers == 0 && text_.empty())
	{
		if (!spaceModeSwitchKeyDown_
			&& (static_cast<uintptr_t>(keyData) & (uintptr_t{ 1 } << 30)) == 0)
		{
			CycleInputMode();
		}
		spaceModeSwitchKeyDown_ = true;
		return true;
	}
	if (wParam != VK_SPACE || !text_.empty())
	{
		spaceModeSwitchKeyDown_ = false;
	}
	if (modifiers == ControlModifier)
	{
		switch (wParam)
		{
		case L'A': SelectAll(); return true;
		case L'C': CopySelectionToClipboard(); return true;
		case L'X': CutSelectionToClipboard(); return true;
		case L'V': PasteFromClipboard(); return true;
		default: break;
		}
	}
	if (controlDown && !hasSystemModifier)
	{
		switch (wParam)
		{
		case VK_LEFT: MoveCaretLeft(shiftDown, true); return true;
		case VK_RIGHT: MoveCaretRight(shiftDown, true); return true;
		case VK_HOME: MoveCaretToStart(shiftDown); return true;
		case VK_END: MoveCaretToEnd(shiftDown); return true;
		case VK_BACK: DeleteBeforeCaret(true); return true;
		case VK_DELETE: DeleteAtCaret(true); return true;
		default: break;
		}
	}
	if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
	{
		Hide();
		return true;
	}
	if (wParam == VK_RETURN && !controlDown && !hasSystemModifier
		&& (modifiers == 0 || modifiers == ShiftModifier)
		&& activateCandidate_
		&& activateCandidate_(shiftDown
			? static_cast<int32_t>(LuvLetterCandidateActionReveal)
			: static_cast<int32_t>(LuvLetterCandidateActionOpen)))
	{
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
	if (!controlDown && !hasSystemModifier
		&& (modifiers == 0 || modifiers == ShiftModifier))
	{
		switch (wParam)
		{
		case VK_DELETE: DeleteAtCaret(); return true;
		case VK_LEFT: MoveCaretLeft(shiftDown); return true;
		case VK_RIGHT: MoveCaretRight(shiftDown); return true;
		case VK_HOME: MoveCaretToStart(shiftDown); return true;
		case VK_END: MoveCaretToEnd(shiftDown); return true;
		case VK_UP:
			if (!shiftDown && moveCandidateSelection_ && moveCandidateSelection_(-1)) return true;
			if (!shiftDown) NavigateHistory(-1);
			return !shiftDown;
		case VK_DOWN:
			if (!shiftDown && moveCandidateSelection_ && moveCandidateSelection_(1)) return true;
			if (!shiftDown) NavigateHistory(1);
			return !shiftDown;
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
		RefreshCaretState(true);
		UpdateImeCompositionWindow();
		Render();
		return 0;
	case WM_KILLFOCUS:
		spaceModeSwitchKeyDown_ = false;
		RefreshCaretState(false, true);
		Render();
		return 0;
	case WM_ACTIVATE:
		RefreshCaretState(
			LOWORD(wParam) != WA_INACTIVE,
			LOWORD(wParam) == WA_INACTIVE);
		Render();
		return 0;
	case WM_ACTIVATEAPP:
		RefreshCaretState(wParam != FALSE, wParam == FALSE);
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
			if (!RefreshCaretState(false))
			{
				Render(true);
				return 0;
			}
			caretVisible_ = !caretVisible_;
			Render(true);
			return 0;
		}
		break;
	case WM_KEYDOWN:
	case WM_SYSKEYDOWN:
		if (!HasKeyboardFocus()) return 0;
		if (HandleKeyDown(wParam, lParam)) return 0;
		break;
	case WM_KEYUP:
		if (wParam == VK_SPACE) spaceModeSwitchKeyDown_ = false;
		break;
	case WM_CHAR:
		if (!HasKeyboardFocus()) return 0;
		if (wParam == L'\b' || wParam == L'\r' || wParam == L'\n' || wParam == L'\t') return 0;
		if (wParam == L' ' && spaceModeSwitchKeyDown_) return 0;
		if (wParam >= 0x20) InsertCharacter(static_cast<wchar_t>(wParam));
		return 0;
	case WM_SYSCHAR:
		return 0;
	case WM_PASTE:
		if (HasKeyboardFocus()) PasteFromClipboard();
		return 0;
	case WM_COPY:
		if (HasKeyboardFocus()) CopySelectionToClipboard();
		return 0;
	case WM_CUT:
		if (HasKeyboardFocus()) CutSelectionToClipboard();
		return 0;
	case WM_CLEAR:
		if (HasKeyboardFocus() && EraseSelection())
		{
			PublishInputChanged();
			Invalidate();
		}
		return 0;
	case WM_LBUTTONDOWN:
		if (visible_)
		{
			SetFocus(hwnd_);
			if (HasKeyboardFocus())
			{
				mouseSelecting_ = true;
				SetCapture(hwnd_);
				SetCaretFromPoint(lParam, IsKeyDown(VK_SHIFT));
			}
		}
		return 0;
	case WM_MOUSEMOVE:
		if (HasKeyboardFocus() && mouseSelecting_ && (wParam & MK_LBUTTON) != 0)
		{
			SetCaretFromPoint(lParam, true);
		}
		return 0;
	case WM_LBUTTONUP:
		if (mouseSelecting_)
		{
			if (HasKeyboardFocus()) SetCaretFromPoint(lParam, true);
			mouseSelecting_ = false;
			if (GetCapture() == hwnd_) ReleaseCapture();
		}
		return 0;
	case WM_CAPTURECHANGED:
		mouseSelecting_ = false;
		return 0;
	case WM_IME_STARTCOMPOSITION:
		if (!HasKeyboardFocus()) return 0;
		UpdateImeCompositionWindow();
		break;
	case WM_IME_COMPOSITION:
		if (!HasKeyboardFocus()) return 0;
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
	case WM_DISPLAYCHANGE:
	case WM_SETTINGCHANGE:
		targetMonitor_ = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
		dpi_ = QueryWindowDpi(hwnd_);
		DiscardResources(true);
		UpdateResponsiveHeight();
		UpdateWindowPosition();
		UpdateWindowShape();
		UpdateImeCompositionWindow();
		if (visible_) Render();
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
		mouseSelecting_ = false;
		shadowWindow_.Detach();
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
