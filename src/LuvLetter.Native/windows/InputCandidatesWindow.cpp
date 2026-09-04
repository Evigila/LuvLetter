#include "windows/InputCandidatesWindow.h"

#include "configuration/NativeConfigurationSanitizer.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <utility>

using namespace LuvLetterNative;

namespace
{
	constexpr int64_t MaxSurfacePixels = 16LL * 1024LL * 1024LL;
	constexpr float RowHeightDip = 38.0f;
	constexpr float OuterPaddingDip = 4.0f;
	constexpr float HorizontalPaddingDip = 10.0f;
	constexpr float IconSizeDip = 16.0f;
	constexpr float IconGapDip = 8.0f;
	constexpr float IconStrokeDip = 1.25f;
	constexpr float WindowGapDip = 7.0f;
	constexpr float CornerRadiusDip = 7.0f;
	constexpr float BorderWidthDip = 1.0f;
	constexpr float PrimaryFontSizeDip = 13.0f;
	constexpr float SecondaryFontSizeDip = 10.0f;

	D2D1_COLOR_F WithOpacity(D2D1_COLOR_F color, float opacity) noexcept
	{
		color.a *= (std::clamp)(opacity, 0.0f, 1.0f);
		return color;
	}

	void DrawFileOutline(
		ID2D1RenderTarget* target,
		const D2D1_RECT_F& bounds,
		ID2D1Brush* brush)
	{
		const auto fold = 4.0f;
		target->DrawLine(D2D1::Point2F(bounds.left, bounds.top),
			D2D1::Point2F(bounds.right - fold, bounds.top), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.right - fold, bounds.top),
			D2D1::Point2F(bounds.right, bounds.top + fold), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.right, bounds.top + fold),
			D2D1::Point2F(bounds.right, bounds.bottom), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.right, bounds.bottom),
			D2D1::Point2F(bounds.left, bounds.bottom), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.left, bounds.bottom),
			D2D1::Point2F(bounds.left, bounds.top), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.right - fold, bounds.top),
			D2D1::Point2F(bounds.right - fold, bounds.top + fold), brush, IconStrokeDip);
		target->DrawLine(D2D1::Point2F(bounds.right - fold, bounds.top + fold),
			D2D1::Point2F(bounds.right, bounds.top + fold), brush, IconStrokeDip);
	}

	void DrawCandidateIcon(
		ID2D1RenderTarget* target,
		LuvLetterCandidateIconKind kind,
		const D2D1_RECT_F& bounds,
		ID2D1Brush* brush)
	{
		const auto left = bounds.left;
		const auto top = bounds.top;
		const auto right = bounds.right;
		const auto bottom = bounds.bottom;
		switch (kind)
		{
		case LuvLetterCandidateIconKindFolder:
			target->DrawLine(D2D1::Point2F(left, top + 4.0f),
				D2D1::Point2F(left + 5.0f, top + 4.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 5.0f, top + 4.0f),
				D2D1::Point2F(left + 7.0f, top + 6.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 7.0f, top + 6.0f),
				D2D1::Point2F(right, top + 6.0f), brush, IconStrokeDip);
			target->DrawRoundedRectangle(
				D2D1::RoundedRect(D2D1::RectF(left, top + 4.0f, right, bottom - 1.0f), 2.0f, 2.0f),
				brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindImage:
			target->DrawRoundedRectangle(
				D2D1::RoundedRect(bounds, 2.0f, 2.0f), brush, IconStrokeDip);
			target->DrawEllipse(
				D2D1::Ellipse(D2D1::Point2F(right - 4.0f, top + 4.0f), 1.4f, 1.4f),
				brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 2.0f, bottom - 3.0f),
				D2D1::Point2F(left + 6.0f, top + 7.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 6.0f, top + 7.0f),
				D2D1::Point2F(left + 9.0f, bottom - 5.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 9.0f, bottom - 5.0f),
				D2D1::Point2F(right - 2.0f, bottom - 2.0f), brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindAudio:
			target->DrawLine(D2D1::Point2F(left + 7.0f, top + 3.0f),
				D2D1::Point2F(right - 2.0f, top + 1.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 7.0f, top + 3.0f),
				D2D1::Point2F(left + 7.0f, bottom - 3.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(right - 2.0f, top + 1.0f),
				D2D1::Point2F(right - 2.0f, bottom - 5.0f), brush, IconStrokeDip);
			target->DrawEllipse(
				D2D1::Ellipse(D2D1::Point2F(left + 4.0f, bottom - 2.5f), 3.0f, 2.0f),
				brush, IconStrokeDip);
			target->DrawEllipse(
				D2D1::Ellipse(D2D1::Point2F(right - 5.0f, bottom - 4.5f), 3.0f, 2.0f),
				brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindVideo:
			target->DrawRoundedRectangle(
				D2D1::RoundedRect(bounds, 2.0f, 2.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 6.0f, top + 4.0f),
				D2D1::Point2F(right - 4.0f, top + 8.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(right - 4.0f, top + 8.0f),
				D2D1::Point2F(left + 6.0f, bottom - 4.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 6.0f, bottom - 4.0f),
				D2D1::Point2F(left + 6.0f, top + 4.0f), brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindExecutable:
		case LuvLetterCandidateIconKindCommand:
			target->DrawRoundedRectangle(
				D2D1::RoundedRect(bounds, 2.0f, 2.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 3.0f, top + 5.0f),
				D2D1::Point2F(left + 6.0f, top + 8.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 6.0f, top + 8.0f),
				D2D1::Point2F(left + 3.0f, bottom - 5.0f), brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 8.0f, bottom - 4.0f),
				D2D1::Point2F(right - 3.0f, bottom - 4.0f), brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindSearch:
			target->DrawEllipse(
				D2D1::Ellipse(D2D1::Point2F(left + 7.0f, top + 7.0f), 5.0f, 5.0f),
				brush, IconStrokeDip);
			target->DrawLine(D2D1::Point2F(left + 10.5f, top + 10.5f),
				D2D1::Point2F(right - 1.0f, bottom - 1.0f), brush, IconStrokeDip);
			break;
		case LuvLetterCandidateIconKindDocument:
		case LuvLetterCandidateIconKindArchive:
		case LuvLetterCandidateIconKindGenericFile:
			DrawFileOutline(target, bounds, brush);
			if (kind == LuvLetterCandidateIconKindDocument)
			{
				target->DrawLine(D2D1::Point2F(left + 3.0f, top + 8.0f),
					D2D1::Point2F(right - 3.0f, top + 8.0f), brush, 1.0f);
				target->DrawLine(D2D1::Point2F(left + 3.0f, top + 11.0f),
					D2D1::Point2F(right - 5.0f, top + 11.0f), brush, 1.0f);
			}
			else if (kind == LuvLetterCandidateIconKindArchive)
			{
				target->DrawLine(D2D1::Point2F(left + 8.0f, top + 5.0f),
					D2D1::Point2F(left + 8.0f, bottom - 2.0f), brush, 1.0f);
				target->DrawLine(D2D1::Point2F(left + 6.5f, top + 8.0f),
					D2D1::Point2F(left + 9.5f, top + 8.0f), brush, 1.0f);
			}
			break;
		default:
			break;
		}
	}
}

InputCandidatesWindow::InputCandidatesWindow(
	ID2D1Factory* d2dFactory,
	IDWriteFactory* dwriteFactory,
	std::function<void(uint64_t, int32_t)> activated)
	: config_(NativeConfigurationSanitizer::DefaultInputBox()),
	  activated_(std::move(activated)),
	  d2dFactory_(d2dFactory),
	  dwriteFactory_(dwriteFactory),
	  surface_(std::make_unique<LayeredWindowSurface>())
{
}

HRESULT InputCandidatesWindow::Attach(HWND window, HWND inputWindow)
{
	if (window == nullptr || inputWindow == nullptr || hwnd_ != nullptr)
	{
		return E_INVALIDARG;
	}
	hwnd_ = window;
	inputHwnd_ = inputWindow;
	dpi_ = QueryWindowDpi(inputHwnd_);
	SetWindowLongPtrW(
		hwnd_,
		GWLP_HWNDPARENT,
		reinterpret_cast<LONG_PTR>(inputHwnd_));
	SetWindowPos(
		hwnd_, HWND_TOPMOST,
		0, 0, PixelWidth(), PixelHeight(),
		SWP_NOMOVE | SWP_NOACTIVATE);
	SetWindowRgn(hwnd_, nullptr, TRUE);
	return S_OK;
}

HRESULT InputCandidatesWindow::EnsureResources()
{
	if (surface_ == nullptr)
	{
		surface_ = std::make_unique<LayeredWindowSurface>();
	}
	auto result = surface_->Ensure(PixelWidth(), PixelHeight(), MaxSurfacePixels);
	if (FAILED(result)) return result;

	if (!renderTarget_)
	{
		const auto properties = D2D1::RenderTargetProperties(
			D2D1_RENDER_TARGET_TYPE_DEFAULT,
			D2D1::PixelFormat(
				DXGI_FORMAT_B8G8R8A8_UNORM,
				D2D1_ALPHA_MODE_PREMULTIPLIED));
		result = d2dFactory_->CreateDCRenderTarget(
			&properties,
			renderTarget_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	renderTarget_->SetDpi(static_cast<float>(dpi_), static_cast<float>(dpi_));

	const auto createFormat = [this](
		float fontSize,
		DWRITE_FONT_WEIGHT weight,
		DWRITE_TEXT_ALIGNMENT alignment,
		IDWriteTextFormat** format) -> HRESULT
	{
		auto createResult = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, weight, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, fontSize, L"", format);
		if (FAILED(createResult)) return createResult;
		(*format)->SetTextAlignment(alignment);
		(*format)->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		(*format)->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
		return S_OK;
	};

	if (!primaryTextFormat_)
	{
		result = createFormat(
			PrimaryFontSizeDip,
			DWRITE_FONT_WEIGHT_SEMI_BOLD,
			DWRITE_TEXT_ALIGNMENT_LEADING,
			primaryTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!secondaryTextFormat_)
	{
		result = createFormat(
			SecondaryFontSizeDip,
			DWRITE_FONT_WEIGHT_NORMAL,
			DWRITE_TEXT_ALIGNMENT_LEADING,
			secondaryTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!backgroundBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.backgroundColor),
			backgroundBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!borderBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.borderColor),
			borderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!textBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(config_.textColor),
			textBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!secondaryTextBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			WithOpacity(ColorFromArgb(config_.textColor), 0.62f),
			secondaryTextBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!selectionBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(0x22C55E, 0.17f),
			selectionBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!separatorBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			WithOpacity(ColorFromArgb(config_.borderColor), 0.28f),
			separatorBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void InputCandidatesWindow::DiscardResources(bool discardSurface)
{
	separatorBrush_.Reset();
	selectionBrush_.Reset();
	secondaryTextBrush_.Reset();
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	secondaryTextFormat_.Reset();
	primaryTextFormat_.Reset();
	renderTarget_.Reset();
	if (discardSurface && surface_ != nullptr)
	{
		surface_->Reset();
	}
}

void InputCandidatesWindow::ApplyConfiguration(const LuvLetterInputBoxConfig& config)
{
	config_ = NativeConfigurationSanitizer::SanitizeInputBox(config);
	DiscardResources(true);
	UpdateGeometry();
	if (visible_) Render();
}

bool InputCandidatesWindow::SetItems(
	std::vector<InputCandidateItem>&& items,
	uint64_t revision,
	uint64_t currentInputRevision,
	bool inputVisible)
{
	if (!state_.Apply(std::move(items), revision, currentInputRevision))
	{
		return false;
	}

	UpdateGeometry();
	if (!inputVisible || state_.IsEmpty())
	{
		Hide();
	}
	else
	{
		Show();
	}
	return true;
}

void InputCandidatesWindow::Clear()
{
	state_.Clear();
	Hide();
}

bool InputCandidatesWindow::MoveSelection(int direction)
{
	if (!visible_ || !state_.MoveSelection(direction))
	{
		return false;
	}
	Render();
	return true;
}

bool InputCandidatesWindow::ActivateSelected(LuvLetterCandidateAction action)
{
	InputCandidateActivation activation{};
	if (!visible_ || !state_.TryActivate(action, activation))
	{
		return false;
	}
	if (activated_)
	{
		activated_(activation.token, static_cast<int32_t>(activation.action));
	}
	return true;
}

void InputCandidatesWindow::Show()
{
	if (hwnd_ == nullptr || inputHwnd_ == nullptr || state_.IsEmpty()) return;
	visible_ = true;
	UpdatePosition();
	Render();
	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
}

void InputCandidatesWindow::Hide()
{
	visible_ = false;
	if (hwnd_ != nullptr)
	{
		ShowWindow(hwnd_, SW_HIDE);
	}
}

void InputCandidatesWindow::SynchronizeToInputWindow()
{
	if (hwnd_ == nullptr || inputHwnd_ == nullptr) return;
	const auto nextDpi = QueryWindowDpi(inputHwnd_);
	if (nextDpi != dpi_)
	{
		dpi_ = nextDpi;
		DiscardResources(true);
	}
	UpdateGeometry();
	if (visible_) Render();
}

void InputCandidatesWindow::UpdateGeometry()
{
	if (hwnd_ == nullptr) return;
	updatingGeometry_ = true;
	SetWindowRgn(hwnd_, nullptr, TRUE);
	UpdatePosition();
	updatingGeometry_ = false;
}

void InputCandidatesWindow::UpdatePosition() const
{
	if (hwnd_ == nullptr || inputHwnd_ == nullptr) return;
	RECT inputBounds{};
	if (!GetWindowRect(inputHwnd_, &inputBounds)) return;
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	const auto gap = DipToPixels(WindowGapDip, dpi_);
	auto y = inputBounds.top - gap - height;
	const auto monitor = MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTONEAREST);
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (monitor != nullptr && GetMonitorInfoW(monitor, &monitorInfo))
	{
		y = (std::max)(y, monitorInfo.rcWork.top);
	}
	SetWindowPos(
		hwnd_, HWND_TOPMOST,
		inputBounds.left, y, width, height,
		SWP_NOACTIVATE);
}

float InputCandidatesWindow::WindowHeightDip() const noexcept
{
	return state_.IsEmpty()
		? 1.0f
		: 2.0f * OuterPaddingDip
			+ static_cast<float>(state_.Items().size()) * RowHeightDip;
}

float InputCandidatesWindow::RenderScaleY() const noexcept
{
	const auto requested = (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
	return (std::clamp)(
		static_cast<float>(PixelHeight()) / static_cast<float>(requested),
		0.0f,
		1.0f);
}

int InputCandidatesWindow::PixelWidth() const
{
	if (inputHwnd_ != nullptr)
	{
		RECT bounds{};
		if (GetWindowRect(inputHwnd_, &bounds) && bounds.right > bounds.left)
		{
			return bounds.right - bounds.left;
		}
	}
	return (std::max)(1, DipToPixels(static_cast<float>(config_.width), dpi_));
}

int InputCandidatesWindow::PixelHeight() const
{
	const auto requested = (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
	if (inputHwnd_ == nullptr) return requested;
	RECT inputBounds{};
	if (!GetWindowRect(inputHwnd_, &inputBounds)) return requested;
	const auto monitor = MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTONEAREST);
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (monitor == nullptr || !GetMonitorInfoW(monitor, &monitorInfo)) return requested;
	const auto gap = DipToPixels(WindowGapDip, dpi_);
	const auto available = (std::max)(
		1L,
		inputBounds.top - monitorInfo.rcWork.top - static_cast<LONG>(gap));
	return (std::min)(requested, static_cast<int>(available));
}

void InputCandidatesWindow::Render()
{
	if (hwnd_ == nullptr || state_.IsEmpty() || FAILED(EnsureResources())) return;
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(renderTarget_->BindDC(surface_->DeviceContext(), &bindRect))) return;

	renderTarget_->BeginDraw();
	renderTarget_->SetTransform(D2D1::Matrix3x2F::Scale(1.0f, RenderScaleY()));
	renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	renderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	renderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto widthDip = static_cast<float>(width) * 96.0f / static_cast<float>(dpi_);
	const auto rounded = CreateInsetRoundedRect(
		0.0f, 0.0f, widthDip, WindowHeightDip(),
		CornerRadiusDip, BorderWidthDip);
	renderTarget_->FillRoundedRectangle(rounded, backgroundBrush_.Get());
	renderTarget_->DrawRoundedRectangle(rounded, borderBrush_.Get(), BorderWidthDip);

	const auto selected = state_.SelectedIndex();
	for (size_t index = 0; index < state_.Items().size(); ++index)
	{
		const auto top = OuterPaddingDip + static_cast<float>(index) * RowHeightDip;
		const auto bottom = top + RowHeightDip;
		if (selected.has_value() && *selected == index)
		{
			const auto selection = D2D1::RoundedRect(
				D2D1::RectF(
					OuterPaddingDip, top,
					widthDip - OuterPaddingDip, bottom),
				4.0f, 4.0f);
			renderTarget_->FillRoundedRectangle(selection, selectionBrush_.Get());
		}
		if (index != 0)
		{
			renderTarget_->DrawLine(
				D2D1::Point2F(HorizontalPaddingDip, top),
				D2D1::Point2F(widthDip - HorizontalPaddingDip, top),
				separatorBrush_.Get(),
				0.6f);
		}

		const auto& item = state_.Items()[index];
		const auto iconTop = top + (RowHeightDip - IconSizeDip) * 0.5f;
		const auto iconBounds = D2D1::RectF(
			HorizontalPaddingDip,
			iconTop,
			HorizontalPaddingDip + IconSizeDip,
			iconTop + IconSizeDip);
		DrawCandidateIcon(
			renderTarget_.Get(), item.iconKind, iconBounds, secondaryTextBrush_.Get());
		const auto textLeft = HorizontalPaddingDip + IconSizeDip + IconGapDip;
		const auto textRight = (std::max)(
			textLeft + 1.0f,
			widthDip - HorizontalPaddingDip);
		const auto hasSecondary = !item.secondaryText.empty();
		const auto primaryRect = hasSecondary
			? D2D1::RectF(textLeft, top + 2.0f, textRight, top + 21.0f)
			: D2D1::RectF(textLeft, top, textRight, bottom);
		renderTarget_->DrawTextW(
			item.primaryText.c_str(),
			static_cast<UINT32>(item.primaryText.size()),
			primaryTextFormat_.Get(), primaryRect, textBrush_.Get(),
			D2D1_DRAW_TEXT_OPTIONS_CLIP,
			DWRITE_MEASURING_MODE_NATURAL);
		if (hasSecondary)
		{
			const auto secondaryRect = D2D1::RectF(
				textLeft, top + 19.0f,
				textRight, bottom - 1.0f);
			renderTarget_->DrawTextW(
				item.secondaryText.c_str(),
				static_cast<UINT32>(item.secondaryText.size()),
				secondaryTextFormat_.Get(), secondaryRect,
				secondaryTextBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}
	}

	const auto endResult = renderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardResources(false);
		return;
	}
	if (SUCCEEDED(endResult))
	{
		surface_->Present(hwnd_, width, height, nullptr, 255);
	}
}

LRESULT InputCandidatesWindow::HandleMessage(
	HWND window,
	UINT message,
	WPARAM wParam,
	LPARAM lParam)
{
	(void)wParam;
	(void)lParam;
	switch (message)
	{
	case WM_ERASEBKGND:
		return 1;
	case WM_MOUSEACTIVATE:
		return MA_NOACTIVATE;
	case WM_NCHITTEST:
		return HTTRANSPARENT;
	case WM_DPICHANGED:
		SynchronizeToInputWindow();
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paint{};
		BeginPaint(window, &paint);
		Render();
		EndPaint(window, &paint);
		return 0;
	}
	case WM_SIZE:
		if (visible_ && !updatingGeometry_) Render();
		return 0;
	case WM_CLOSE:
		Hide();
		return 0;
	case WM_DESTROY:
		DiscardResources(true);
		hwnd_ = nullptr;
		inputHwnd_ = nullptr;
		visible_ = false;
		return 0;
	default:
		return DefWindowProcW(window, message, wParam, lParam);
	}
}
