#include "windows/InputCandidatesWindow.h"

#include "configuration/NativeConfigurationSanitizer.h"
#include "rendering/SurfaceStyleDefaults.h"
#include "windows/CandidateActionPresentation.h"

#include <algorithm>
#include <cmath>
#include <cwchar>
#include <utility>

using namespace LuvLetterNative;

namespace
{
	constexpr int64_t MaxSurfacePixels = 16LL * 1024LL * 1024LL;
	constexpr float RowVerticalPaddingDip = 4.0f;
	constexpr float RowHeightDip = 2.0f * SurfaceLineHeightDip
		+ 2.0f * RowVerticalPaddingDip;
	constexpr float OuterPaddingDip = 6.0f;
	constexpr float HorizontalPaddingDip = 12.0f;
	constexpr float IconSizeDip = SurfaceLineHeightDip;
	constexpr float IconGapDip = 10.0f;
	constexpr float IconStrokeDip = 1.25f;
	constexpr float ActionHintGapDip = 10.0f;
	constexpr float ActionIconSizeDip = 18.0f;
	constexpr float ActionIconGapDip = 7.0f;
	constexpr float WindowGapDip = 7.0f;
	constexpr float CornerRadiusDip = 7.0f;
	constexpr float BorderWidthDip = 1.0f;
	void DrawEnterIcon(
		ID2D1RenderTarget* target,
		const D2D1_RECT_F& bounds,
		ID2D1Brush* brush)
	{
		if (target == nullptr || brush == nullptr) return;

		constexpr float ViewBoxSize = 276.0f;
		const auto width = (std::max)(0.0f, bounds.right - bounds.left);
		const auto height = (std::max)(0.0f, bounds.bottom - bounds.top);
		const auto scale = (std::min)(width, height) / ViewBoxSize;
		if (scale <= 0.0f) return;
		const auto offsetX = bounds.left + (width - ViewBoxSize * scale) * 0.5f;
		const auto offsetY = bounds.top + (height - ViewBoxSize * scale) * 0.5f;
		const auto point = [scale, offsetX, offsetY](float x, float y)
		{
			return D2D1::Point2F(offsetX + x * scale, offsetY + y * scale);
		};

		Microsoft::WRL::ComPtr<ID2D1Factory> factory;
		target->GetFactory(factory.GetAddressOf());
		if (!factory) return;

		D2D1_STROKE_STYLE_PROPERTIES strokeProperties{};
		strokeProperties.startCap = D2D1_CAP_STYLE_ROUND;
		strokeProperties.endCap = D2D1_CAP_STYLE_ROUND;
		strokeProperties.dashCap = D2D1_CAP_STYLE_ROUND;
		strokeProperties.lineJoin = D2D1_LINE_JOIN_ROUND;
		strokeProperties.miterLimit = 10.0f;
		strokeProperties.dashStyle = D2D1_DASH_STYLE_SOLID;
		Microsoft::WRL::ComPtr<ID2D1StrokeStyle> strokeStyle;
		if (FAILED(factory->CreateStrokeStyle(
			strokeProperties, nullptr, 0, strokeStyle.GetAddressOf()))) return;

		const auto strokeWidth = 10.0f * scale;
		const auto frame = D2D1::RoundedRect(
			D2D1::RectF(
				offsetX + 34.0f * scale,
				offsetY + 30.0f * scale,
				offsetX + 242.0f * scale,
				offsetY + 240.0f * scale),
			38.0f * scale,
			38.0f * scale);
		target->DrawRoundedRectangle(frame, brush, strokeWidth, strokeStyle.Get());

		Microsoft::WRL::ComPtr<ID2D1PathGeometry> arrow;
		if (FAILED(factory->CreatePathGeometry(arrow.GetAddressOf()))) return;
		Microsoft::WRL::ComPtr<ID2D1GeometrySink> sink;
		if (FAILED(arrow->Open(sink.GetAddressOf()))) return;
		sink->BeginFigure(point(192.0f, 95.0f), D2D1_FIGURE_BEGIN_HOLLOW);
		sink->AddLine(point(192.0f, 132.0f));
		sink->AddQuadraticBezier(D2D1::QuadraticBezierSegment(
			point(192.0f, 144.0f), point(180.0f, 144.0f)));
		sink->AddLine(point(84.0f, 144.0f));
		sink->EndFigure(D2D1_FIGURE_END_OPEN);
		sink->BeginFigure(point(84.0f, 144.0f), D2D1_FIGURE_BEGIN_HOLLOW);
		sink->AddLine(point(113.0f, 114.0f));
		sink->EndFigure(D2D1_FIGURE_END_OPEN);
		sink->BeginFigure(point(84.0f, 144.0f), D2D1_FIGURE_BEGIN_HOLLOW);
		sink->AddLine(point(113.0f, 174.0f));
		sink->EndFigure(D2D1_FIGURE_END_OPEN);
		if (FAILED(sink->Close())) return;
		target->DrawGeometry(arrow.Get(), brush, strokeWidth, strokeStyle.Get());
	}

	float MeasureTextWidth(
		IDWriteFactory* factory,
		IDWriteTextFormat* format,
		const wchar_t* text) noexcept
	{
		if (factory == nullptr || format == nullptr || text == nullptr) return 0.0f;
		const auto length = static_cast<UINT32>(wcslen(text));
		const auto fallbackWidth = static_cast<float>(length) * SurfaceFontSizeDip;
		Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
		if (FAILED(factory->CreateTextLayout(
			text,
			length,
			format,
			4096.0f,
			SurfaceLineHeightDip,
			layout.GetAddressOf()))) return fallbackWidth;
		DWRITE_TEXT_METRICS metrics{};
		return SUCCEEDED(layout->GetMetrics(&metrics))
			? std::ceil(metrics.widthIncludingTrailingWhitespace)
			: fallbackWidth;
	}

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
	surface_(std::make_unique<LayeredWindowSurface>()),
	shadowWindow_(d2dFactory)
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
	(void)iconLoader_.Attach(hwnd_);
	(void)shadowWindow_.Attach(hwnd_);
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
		Microsoft::WRL::ComPtr<IDWriteTextFormat> nextFormat;
		auto createResult = dwriteFactory_->CreateTextFormat(
			SurfaceFontFamily, nullptr, weight, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, fontSize, L"", nextFormat.GetAddressOf());
		if (FAILED(createResult)) return createResult;
		createResult = nextFormat->SetTextAlignment(alignment);
		if (FAILED(createResult)) return createResult;
		createResult = nextFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		if (FAILED(createResult)) return createResult;
		createResult = nextFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
		if (FAILED(createResult)) return createResult;
		DWRITE_TRIMMING trimming{};
		trimming.granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER;
		Microsoft::WRL::ComPtr<IDWriteInlineObject> trimmingSign;
		createResult = dwriteFactory_->CreateEllipsisTrimmingSign(
			nextFormat.Get(),
			trimmingSign.GetAddressOf());
		if (FAILED(createResult)) return createResult;
		createResult = nextFormat->SetTrimming(&trimming, trimmingSign.Get());
		if (FAILED(createResult)) return createResult;
		*format = nextFormat.Detach();
		return S_OK;
	};

	if (!primaryTextFormat_)
	{
		result = createFormat(
			SurfaceFontSizeDip,
			DWRITE_FONT_WEIGHT_SEMI_BOLD,
			DWRITE_TEXT_ALIGNMENT_LEADING,
			primaryTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!fileNameTextFormat_)
	{
		result = createFormat(
			SurfaceFontSizeDip,
			DWRITE_FONT_WEIGHT_BOLD,
			DWRITE_TEXT_ALIGNMENT_LEADING,
			fileNameTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!secondaryTextFormat_)
	{
		result = createFormat(
			SurfaceFontSizeDip,
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
	iconBitmaps_.clear();
	separatorBrush_.Reset();
	selectionBrush_.Reset();
	secondaryTextBrush_.Reset();
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	secondaryTextFormat_.Reset();
	fileNameTextFormat_.Reset();
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
	keyboardModifiers_ = GetCurrentHotkeyModifiers()
		& (ArkheideSystem::CandidateControlModifier | ArkheideSystem::CandidateShiftModifier);
	if (!state_.Apply(std::move(items), revision, currentInputRevision))
	{
		return false;
	}

	AdvanceIconGeneration();
	QueueCandidateIcons();
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
	AdvanceIconGeneration();
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

void InputCandidatesWindow::SetKeyboardModifiers(int modifiers)
{
	const auto normalized = modifiers
		& (ArkheideSystem::CandidateControlModifier | ArkheideSystem::CandidateShiftModifier);
	if (keyboardModifiers_ == normalized) return;
	keyboardModifiers_ = normalized;
	if (visible_) Render();
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
	shadowWindow_.Hide();
}

void InputCandidatesWindow::SynchronizeToInputWindow()
{
	if (hwnd_ == nullptr || inputHwnd_ == nullptr) return;
	const auto nextDpi = QueryWindowDpi(inputHwnd_);
	if (nextDpi != dpi_)
	{
		dpi_ = nextDpi;
		DiscardResources(true);
		AdvanceIconGeneration();
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
	const auto x = inputBounds.left;
	const auto monitor = MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTONEAREST);
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (monitor != nullptr && GetMonitorInfoW(monitor, &monitorInfo))
	{
		const auto availableAbove = (std::max)(
			0L,
			inputBounds.top - monitorInfo.rcWork.top - static_cast<LONG>(gap));
		const auto availableBelow = (std::max)(
			0L,
			monitorInfo.rcWork.bottom - inputBounds.bottom - static_cast<LONG>(gap));
		const auto placeAbove = availableAbove >= height
			|| (availableBelow < height && availableAbove >= availableBelow);
		y = placeAbove
			? inputBounds.top - gap - height
			: inputBounds.bottom + gap;
		y = (std::clamp)(
			y,
			monitorInfo.rcWork.top,
			(std::max)(monitorInfo.rcWork.top, monitorInfo.rcWork.bottom - height));
	}
	SetWindowPos(
		hwnd_, HWND_TOPMOST,
		x, y, width, height,
		SWP_NOACTIVATE);
}

float InputCandidatesWindow::WindowHeightDip() const noexcept
{
	return state_.IsEmpty()
		? 1.0f
		: 2.0f * OuterPaddingDip
			+ static_cast<float>(state_.Items().size()) * RowHeightDip;
}

int InputCandidatesWindow::PixelWidth() const
{
	if (inputHwnd_ != nullptr)
	{
		RECT bounds{};
		if (GetWindowRect(inputHwnd_, &bounds) && bounds.right > bounds.left)
		{
			return static_cast<int>(bounds.right - bounds.left);
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
	const auto availableAbove = (std::max)(
		0L,
		inputBounds.top - monitorInfo.rcWork.top - static_cast<LONG>(gap));
	const auto availableBelow = (std::max)(
		0L,
		monitorInfo.rcWork.bottom - inputBounds.bottom - static_cast<LONG>(gap));
	const auto available = (std::max)(1L, (std::max)(availableAbove, availableBelow));
	if (requested <= available) return requested;
	const auto workHeight = (std::max)(
		1L,
		monitorInfo.rcWork.bottom - monitorInfo.rcWork.top);
	const auto minimumReadable = (std::min)(
		requested,
		(std::max)(
			1,
			DipToPixels(2.0f * OuterPaddingDip + RowHeightDip, dpi_)));
	if (available < minimumReadable)
	{
		return (std::min)(minimumReadable, static_cast<int>(workHeight));
	}

	const auto availableDip = PixelsToDip(static_cast<int>(available), dpi_);
	const auto rowCapacity = static_cast<int>((std::max)(
		0.0f,
		availableDip - 2.0f * OuterPaddingDip) / RowHeightDip);
	if (rowCapacity <= 0) return static_cast<int>(available);
	const auto fittedHeightDip = 2.0f * OuterPaddingDip
		+ static_cast<float>(rowCapacity) * RowHeightDip;
	return (std::min)(
		static_cast<int>(available),
		(std::max)(1, DipToPixels(fittedHeightDip, dpi_)));
}

void InputCandidatesWindow::AdvanceIconGeneration() noexcept
{
	iconBitmaps_.clear();
	if (++iconGeneration_ == 0) ++iconGeneration_;
	iconLoader_.BeginGeneration(iconGeneration_);
}

std::optional<ArkheideSystem::ShellIconSourceKind> InputCandidatesWindow::IconSourceKind(
	const InputCandidateItem& item) noexcept
{
	if (!item.iconSource.empty())
	{
		return ArkheideSystem::ShellIconSourceKind::ShellItem;
	}
	if (item.kind != LuvLetterCandidateKindFile)
	{
		return std::nullopt;
	}
	if (item.iconKind == LuvLetterCandidateIconKindFolder)
	{
		return ArkheideSystem::ShellIconSourceKind::Folder;
	}
	if (item.iconKind == LuvLetterCandidateIconKindExecutable)
	{
		return ArkheideSystem::ShellIconSourceKind::Application;
	}
	return ArkheideSystem::ShellIconSourceKind::GenericFile;
}

void InputCandidatesWindow::QueueCandidateIcons() noexcept
{
	if (iconGeneration_ == 0) return;
	const auto pixelSize = static_cast<UINT>((std::max)(
		1,
		DipToPixels(IconSizeDip, dpi_)));
	for (const auto& item : state_.Items())
	{
		if (iconBitmaps_.contains(item.token)) continue;
		const auto sourceKind = IconSourceKind(item);
		if (!sourceKind.has_value()) continue;
		iconLoader_.Request(
			item.token,
			iconGeneration_,
			*sourceKind,
			item.iconSource,
			pixelSize);
	}
}

bool InputCandidatesWindow::ApplyCompletedIcons() noexcept
{
	const auto results = iconLoader_.TakeCompleted();
	if (!visible_ || results.empty() || FAILED(EnsureResources())) return false;
	const auto expectedPixelSize = static_cast<UINT>((std::max)(
		1,
		DipToPixels(IconSizeDip, dpi_)));
	bool changed = false;
	for (const auto& result : results)
	{
		if (result.generation != iconGeneration_
			|| result.pixelSize != expectedPixelSize
			|| !result.bitmap
			|| result.bitmap->width != result.pixelSize
			|| result.bitmap->height != result.pixelSize
			|| result.bitmap->stride != result.pixelSize * 4U
			|| result.bitmap->pixels.size()
				!= static_cast<size_t>(result.bitmap->stride) * result.bitmap->height)
		{
			continue;
		}

		const auto item = std::ranges::find_if(
			state_.Items(),
			[&result](const InputCandidateItem& candidate)
			{
				return candidate.token == result.token;
			});
		if (item == state_.Items().end()) continue;
		const auto expectedSourceKind = IconSourceKind(*item);
		if (!expectedSourceKind.has_value()
			|| *expectedSourceKind != result.sourceKind
			|| (!item->iconSource.empty() && CompareStringOrdinal(
				item->iconSource.c_str(),
				static_cast<int>(item->iconSource.size()),
				result.source.c_str(),
				static_cast<int>(result.source.size()),
				TRUE) != CSTR_EQUAL))
		{
			continue;
		}

		Microsoft::WRL::ComPtr<ID2D1Bitmap> bitmap;
		const auto properties = D2D1::BitmapProperties(
			D2D1::PixelFormat(
				DXGI_FORMAT_B8G8R8A8_UNORM,
				D2D1_ALPHA_MODE_PREMULTIPLIED),
			static_cast<float>(dpi_),
			static_cast<float>(dpi_));
		if (FAILED(renderTarget_->CreateBitmap(
			D2D1::SizeU(result.bitmap->width, result.bitmap->height),
			result.bitmap->pixels.data(),
			result.bitmap->stride,
			properties,
			bitmap.GetAddressOf())))
		{
			continue;
		}
		try
		{
			iconBitmaps_.insert_or_assign(
				result.token,
				CandidateIconBitmap{
					result.generation,
					result.sourceKind,
					result.source,
					result.pixelSize,
					std::move(bitmap),
				});
			changed = true;
		}
		catch (...)
		{
			// The existing vector glyph remains a valid low-memory fallback.
		}
	}
	return changed;
}

void InputCandidatesWindow::Render()
{
	if (hwnd_ == nullptr || state_.IsEmpty() || FAILED(EnsureResources())) return;
	QueueCandidateIcons();
	const auto width = PixelWidth();
	const auto height = PixelHeight();
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(renderTarget_->BindDC(surface_->DeviceContext(), &bindRect))) return;

	renderTarget_->BeginDraw();
	renderTarget_->SetTransform(D2D1::Matrix3x2F::Identity());
	renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	renderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	renderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto widthDip = static_cast<float>(width) * 96.0f / static_cast<float>(dpi_);
	const auto heightDip = static_cast<float>(height) * 96.0f / static_cast<float>(dpi_);
	const auto rounded = CreateInsetRoundedRect(
		0.0f, 0.0f, widthDip, heightDip,
		CornerRadiusDip, BorderWidthDip);
	renderTarget_->FillRoundedRectangle(rounded, backgroundBrush_.Get());
	renderTarget_->DrawRoundedRectangle(rounded, borderBrush_.Get(), BorderWidthDip);

	const auto selected = state_.SelectedIndex();
	const auto availableRows = (std::max)(
		size_t{ 1 },
		static_cast<size_t>((std::max)(
			0.0f,
			heightDip - 2.0f * OuterPaddingDip) / RowHeightDip));
	const auto visibleRows = (std::min)(availableRows, state_.Items().size());
	auto firstVisibleIndex = size_t{ 0 };
	if (selected.has_value() && *selected >= visibleRows)
	{
		firstVisibleIndex = *selected - visibleRows + 1;
	}
	if (state_.Items().size() > visibleRows)
	{
		firstVisibleIndex = (std::min)(
			firstVisibleIndex,
			state_.Items().size() - visibleRows);
	}
	for (size_t visibleIndex = 0; visibleIndex < visibleRows; ++visibleIndex)
	{
		const auto index = firstVisibleIndex + visibleIndex;
		const auto top = OuterPaddingDip + static_cast<float>(visibleIndex) * RowHeightDip;
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
		if (visibleIndex != 0)
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
		if (const auto icon = iconBitmaps_.find(item.token);
			icon != iconBitmaps_.end() && icon->second.bitmap)
		{
			renderTarget_->DrawBitmap(
				icon->second.bitmap.Get(),
				iconBounds,
				1.0f,
				D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
		}
		else
		{
			DrawCandidateIcon(
				renderTarget_.Get(), item.iconKind, iconBounds, secondaryTextBrush_.Get());
		}
		const auto textLeft = HorizontalPaddingDip + IconSizeDip + IconGapDip;
		const auto actionLabel = selected.has_value() && *selected == index
			? ArkheideSystem::CandidateActionLabel(item.actions, keyboardModifiers_)
			: nullptr;
		const auto actionRight = (std::max)(
			textLeft + 1.0f,
			widthDip - HorizontalPaddingDip);
		const auto actionTextWidth = actionLabel == nullptr
			? 0.0f
			: MeasureTextWidth(dwriteFactory_.Get(), secondaryTextFormat_.Get(), actionLabel);
		const auto actionWidth = actionLabel == nullptr
			? 0.0f
			: ActionIconSizeDip + ActionIconGapDip + actionTextWidth;
		const auto actionLeft = actionLabel == nullptr
			? actionRight
			: (std::max)(textLeft + 1.0f, actionRight - actionWidth);
		const auto textRight = (std::max)(
			textLeft + 1.0f,
			actionLabel == nullptr ? actionRight : actionLeft - ActionHintGapDip);
		const auto hasSecondary = !item.secondaryText.empty();
		const auto primaryRect = hasSecondary
			? D2D1::RectF(
				textLeft,
				top + RowVerticalPaddingDip,
				textRight,
				top + RowVerticalPaddingDip + SurfaceLineHeightDip)
			: D2D1::RectF(textLeft, top, textRight, bottom);
		const auto primaryFormat = item.kind == LuvLetterCandidateKindFile
			? fileNameTextFormat_.Get()
			: primaryTextFormat_.Get();
		renderTarget_->DrawTextW(
			item.primaryText.c_str(),
			static_cast<UINT32>(item.primaryText.size()),
			primaryFormat, primaryRect, textBrush_.Get(),
			D2D1_DRAW_TEXT_OPTIONS_CLIP,
			DWRITE_MEASURING_MODE_NATURAL);
		if (hasSecondary)
		{
			const auto secondaryRect = D2D1::RectF(
				textLeft, top + RowVerticalPaddingDip + SurfaceLineHeightDip,
				textRight, bottom - RowVerticalPaddingDip);
			renderTarget_->DrawTextW(
				item.secondaryText.c_str(),
				static_cast<UINT32>(item.secondaryText.size()),
				secondaryTextFormat_.Get(), secondaryRect,
				secondaryTextBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}
		if (actionLabel != nullptr)
		{
			const auto iconTop = top + (RowHeightDip - ActionIconSizeDip) * 0.5f;
			const auto actionIconBounds = D2D1::RectF(
				actionLeft,
				iconTop,
				actionLeft + ActionIconSizeDip,
				iconTop + ActionIconSizeDip);
			DrawEnterIcon(
				renderTarget_.Get(),
				actionIconBounds,
				secondaryTextBrush_.Get());
			const auto actionTextLeft = actionIconBounds.right + ActionIconGapDip;
			const auto actionTextRect = D2D1::RectF(
				actionTextLeft,
				top,
				actionRight,
				bottom);
			renderTarget_->DrawTextW(
				actionLabel,
				static_cast<UINT32>(wcslen(actionLabel)),
				secondaryTextFormat_.Get(),
				actionTextRect,
				secondaryTextBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
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
		if (surface_->Present(hwnd_, width, height, nullptr, 255))
		{
			(void)shadowWindow_.Update(
				dpi_,
				{ LuvLetterNative::SurfaceShadowShape{
					D2D1::RectF(0.0f, 0.0f, widthDip, heightDip),
					CornerRadiusDip,
					1.0f } });
		}
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
	case ArkheideSystem::ShellIconLoader::CompletionMessage:
		if (ApplyCompletedIcons()) Render();
		return 0;
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
		if (visible_) Render();
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
		iconLoader_.Shutdown();
		shadowWindow_.Detach();
		DiscardResources(true);
		hwnd_ = nullptr;
		inputHwnd_ = nullptr;
		visible_ = false;
		return 0;
	default:
		return DefWindowProcW(window, message, wParam, lParam);
	}
}
