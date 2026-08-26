#include "windows/QuickActionsWindow.h"

#include "configuration/NativeConfigurationSanitizer.h"

#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <utility>

using namespace LuvLetterNative;

namespace
{
	constexpr int64_t MaxSurfacePixels = 16LL * 1024LL * 1024LL;
}

QuickActionsWindow::QuickActionsWindow(
	ID2D1Factory* d2dFactory,
	IDWriteFactory* dwriteFactory,
	std::function<void(uint64_t)> activated)
	: config_(NativeConfigurationSanitizer::DefaultQuickActionsWindow()),
	itemsPerPage_(static_cast<size_t>(config_.itemsPerPage)),
	activated_(std::move(activated)),
	d2dFactory_(d2dFactory),
	dwriteFactory_(dwriteFactory),
	surface_(std::make_unique<LayeredWindowSurface>())
{
}

HRESULT QuickActionsWindow::Attach(HWND window)
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

HRESULT QuickActionsWindow::EnsureResources()
{
	HRESULT result = S_OK;
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	(void)renderScale;
	if (surface_ == nullptr)
	{
		surface_ = std::make_unique<LuvLetterNative::LayeredWindowSurface>();
	}
	result = surface_->Ensure(width, height, MaxSurfacePixels);
	if (FAILED(result)) return result;
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
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize, L"", textFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_WHOLE_WORD);
	}
	if (!numberFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize * 1.55f, L"", numberFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		numberFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
		numberFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		numberFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
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
	if (!accentBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.accentColor), accentBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void QuickActionsWindow::DiscardResources(bool discardSurface)
{
	accentBrush_.Reset();
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	numberFormat_.Reset();
	textFormat_.Reset();
	renderTarget_.Reset();
	if (discardSurface && surface_ != nullptr)
	{
		surface_->Reset();
	}
}

void QuickActionsWindow::ApplyConfiguration(const LuvLetterFeatureWindowConfig& config)
{
	config_ = NativeConfigurationSanitizer::SanitizeQuickActionsWindow(config);
	SetItemsPerPage(static_cast<size_t>(config_.itemsPerPage));
	DiscardResources(true);
	UpdateGeometry();
	if (visible_) Render();
}

void QuickActionsWindow::SetItems(std::vector<QuickActionItem>&& items)
{
	items_ = std::move(items);
	ResetPaging(static_cast<size_t>(config_.itemsPerPage));
	if (items_.empty())
	{
		Hide();
	}
	DiscardResources(true);
	UpdateGeometry();
	if (visible_) Render();
}

void QuickActionsWindow::UpdateWindowShape() const
{
	if (hwnd_ != nullptr)
	{
		// Alpha-zero gaps and corners of an UpdateLayeredWindow bitmap are already
		// transparent to hit testing; an integer HRGN would only reintroduce stairs.
		SetWindowRgn(hwnd_, nullptr, TRUE);
	}
}

void QuickActionsWindow::RefreshDpiFromWindow()
{
	const auto dpi = QueryWindowDpi(hwnd_);
	if (dpi == dpi_) return;
	dpi_ = dpi;
	DiscardResources(true);
}

void QuickActionsWindow::ApplyDpiChange(UINT dpi, const RECT* suggestedRect)
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
		UpdatePosition();
	}
	UpdateWindowShape();
	if (visible_) Render();
}

void QuickActionsWindow::Show(HMONITOR targetMonitor)
{
	if (hwnd_ == nullptr || items_.empty()) return;
	targetMonitor_ = targetMonitor;
	// See ShowInputWindowAndFocus: move once to obtain the window's target DPI,
	// then perform the configured DIP-based placement with that DPI.
	UpdatePosition();
	RefreshDpiFromWindow();
	UpdateGeometry();
	visible_ = true;
	ShowWindow(hwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(hwnd_);
	SetFocus(hwnd_);
	Render();
}

void QuickActionsWindow::Hide()
{
	if (hwnd_ == nullptr) return;
	visible_ = false;
	ShowWindow(hwnd_, SW_HIDE);
}

void QuickActionsWindow::UpdateGeometry()
{
	if (hwnd_ == nullptr) return;
	updatingGeometry_ = true;
	UpdateWindowShape();
	UpdatePosition();
	updatingGeometry_ = false;
}

void QuickActionsWindow::UpdatePosition() const
{
	if (hwnd_ == nullptr) return;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return;
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	(void)renderScale;
	const auto workWidth = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
	LONG x = monitorInfo.rcWork.left + ((std::max)(0L, workWidth - static_cast<LONG>(width)) / 2);
	LONG y = monitorInfo.rcWork.bottom - height
		- DipToPixels(static_cast<float>(config_.bottomMargin), dpi_);
	x += DipToPixels(static_cast<float>(config_.offsetX), dpi_);
	y += DipToPixels(static_cast<float>(config_.offsetY), dpi_);
	SetWindowPos(hwnd_, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
}

float QuickActionsWindow::WindowWidthDip() const
{
	const auto count = (std::max)(size_t{ 1 }, CurrentItemCount());
	return (std::max)(1.0f,
		static_cast<float>(count) * config_.cellSize
		+ static_cast<float>(count - 1) * config_.gap);
}

void QuickActionsWindow::GetSurfaceMetrics(int& width, int& height, float& renderScale) const
{
	const auto requestedWidth = (std::max)(
		1,
		DipToPixels(WindowWidthDip(), dpi_));
	const auto requestedHeight = (std::max)(
		1,
		DipToPixels(config_.cellSize, dpi_));

	int availableWidth = requestedWidth;
	int availableHeight = requestedHeight;
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (hwnd_ != nullptr
			? MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST)
			: MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY));
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (monitor != nullptr && GetMonitorInfoW(monitor, &monitorInfo))
	{
		const auto workWidth = (std::clamp)(
			static_cast<int64_t>(monitorInfo.rcWork.right)
				- static_cast<int64_t>(monitorInfo.rcWork.left),
			int64_t{ 1 },
			static_cast<int64_t>((std::numeric_limits<int>::max)()));
		const auto workHeight = (std::clamp)(
			static_cast<int64_t>(monitorInfo.rcWork.bottom)
				- static_cast<int64_t>(monitorInfo.rcWork.top),
			int64_t{ 1 },
			static_cast<int64_t>((std::numeric_limits<int>::max)()));
		const auto margin = (std::clamp)(
			static_cast<int64_t>(DipToPixels(
				static_cast<float>(config_.bottomMargin),
				dpi_)),
			int64_t{ 0 },
			workHeight - 1);
		availableWidth = static_cast<int>((std::min)(
			static_cast<int64_t>(requestedWidth),
			workWidth));
		availableHeight = static_cast<int>((std::min)(
			static_cast<int64_t>(requestedHeight),
			workHeight - margin));
	}

	const auto requestedPixels = static_cast<double>(requestedWidth)
		* static_cast<double>(requestedHeight);
	auto scale = (std::min)({
		1.0,
		static_cast<double>(availableWidth) / static_cast<double>(requestedWidth),
		static_cast<double>(availableHeight) / static_cast<double>(requestedHeight),
		std::sqrt(static_cast<double>(MaxSurfacePixels) / requestedPixels),
	});
	scale = (std::clamp)(scale, 0.0, 1.0);
	width = (std::clamp)(
		static_cast<int>(std::floor(static_cast<double>(requestedWidth) * scale)),
		1,
		availableWidth);
	height = (std::clamp)(
		static_cast<int>(std::floor(static_cast<double>(requestedHeight) * scale)),
		1,
		availableHeight);
	if (static_cast<int64_t>(width)
		> MaxSurfacePixels / static_cast<int64_t>(height))
	{
		width = static_cast<int>(MaxSurfacePixels / static_cast<int64_t>(height));
	}
	renderScale = static_cast<float>((std::min)(
		static_cast<double>(width) / static_cast<double>(requestedWidth),
		static_cast<double>(height) / static_cast<double>(requestedHeight)));
}

int QuickActionsWindow::PixelWidth() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	return width;
}

int QuickActionsWindow::PixelHeight() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	return height;
}

void QuickActionsWindow::Render()
{
	if (hwnd_ == nullptr || items_.empty() || FAILED(EnsureResources())) return;
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(renderTarget_->BindDC(surface_->DeviceContext(), &bindRect))) return;
	renderTarget_->BeginDraw();
	renderTarget_->SetTransform(D2D1::Matrix3x2F::Scale(renderScale, renderScale));
	renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	renderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	renderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto count = CurrentItemCount();
	const auto start = FirstItemIndex();
	for (size_t index = 0; index < count; ++index)
	{
		const auto left = static_cast<float>(index) * (config_.cellSize + config_.gap);
		const auto rounded = CreateInsetRoundedRect(
			left,
			0.0f,
			left + config_.cellSize,
			config_.cellSize,
			config_.cornerRadius,
			config_.borderThickness);
		renderTarget_->FillRoundedRectangle(rounded, backgroundBrush_.Get());
		if (config_.borderThickness > 0.0f)
		{
			renderTarget_->DrawRoundedRectangle(
				rounded, borderBrush_.Get(), config_.borderThickness);
		}
		const wchar_t number[] = {
			static_cast<wchar_t>(config_.firstItemVirtualKey + index),
			L'\0'
		};
		const auto inset = (std::max)(6.0f, config_.cellSize * 0.08f);
		const auto numberRect = D2D1::RectF(
			left + inset, inset,
			left + config_.cellSize - inset, config_.cellSize * 0.52f);
		const auto labelRect = D2D1::RectF(
			left + inset, config_.cellSize * 0.42f,
			left + config_.cellSize - inset, config_.cellSize - inset);
		renderTarget_->DrawTextW(number, 1, numberFormat_.Get(), numberRect, accentBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
		const auto& label = items_[start + index].label;
		renderTarget_->DrawTextW(
			label.c_str(), static_cast<UINT32>(label.size()), textFormat_.Get(), labelRect,
			textBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
	}
	const auto endResult = renderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardResources(false);
		return;
	}
	if (SUCCEEDED(endResult))
	{
		surface_->Present(hwnd_, width, height);
	}
}

bool QuickActionsWindow::HandleKeyDown(WPARAM wParam)
{
	if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
	{
		Hide();
		return true;
	}
	if (MatchesHotkey(wParam, config_.previousVirtualKey, config_.previousModifiers))
	{
		ChangePage(-1);
		return true;
	}
	if (MatchesHotkey(wParam, config_.nextVirtualKey, config_.nextModifiers))
	{
		ChangePage(1);
		return true;
	}
	if (GetCurrentHotkeyModifiers() == 0)
	{
		const auto firstKey = static_cast<WPARAM>(config_.firstItemVirtualKey);
		if (wParam >= firstKey && wParam < firstKey + static_cast<WPARAM>(config_.itemsPerPage))
		{
			Activate(static_cast<size_t>(wParam - firstKey));
			return true;
		}
		if (config_.firstItemVirtualKey >= L'0'
			&& config_.firstItemVirtualKey <= L'9'
			&& wParam >= VK_NUMPAD0
			&& wParam <= VK_NUMPAD9)
		{
			const auto firstNumpadKey = static_cast<WPARAM>(
				VK_NUMPAD0 + config_.firstItemVirtualKey - L'0');
			if (wParam >= firstNumpadKey
				&& wParam < firstNumpadKey + static_cast<WPARAM>(config_.itemsPerPage))
			{
				Activate(static_cast<size_t>(wParam - firstNumpadKey));
				return true;
			}
		}
	}
	return false;
}

void QuickActionsWindow::ChangePage(int direction)
{
	if (!MovePage(direction)) return;
	UpdateGeometry();
	Render();
}

void QuickActionsWindow::Activate(size_t indexOnPage)
{
	size_t absoluteIndex = 0;
	if (!TryResolveIndex(indexOnPage, absoluteIndex)) return;
	const auto token = items_[absoluteIndex].token;
	Hide();
	if (activated_) activated_(token);
}

LRESULT QuickActionsWindow::HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND: return 1;
	case WM_KEYDOWN:
	case WM_SYSKEYDOWN:
		if ((lParam & (1LL << 30)) != 0) return 0;
		if (HandleKeyDown(wParam)) return 0;
		break;
	case WM_SYSCHAR:
		return 0;
	case WM_LBUTTONDOWN:
	{
		SetFocus(hwnd_);
		int width = 0;
		int height = 0;
		float renderScale = 1.0f;
		GetSurfaceMetrics(width, height, renderScale);
		const auto x = PixelsToDip(GET_X_LPARAM(lParam), dpi_)
			/ (std::max)(renderScale, 0.0001f);
		const auto stride = config_.cellSize + config_.gap;
		if (stride > 0.0f)
		{
			const auto index = static_cast<size_t>((std::max)(0.0f, std::floor(x / stride)));
			const auto withinCell = x - static_cast<float>(index) * stride;
			if (withinCell <= config_.cellSize) Activate(index);
		}
		return 0;
	}
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
		if (visible_ && !updatingGeometry_) Render();
		return 0;
	case WM_CLOSE:
		Hide();
		return 0;
	case WM_DESTROY:
		DiscardResources(true);
		hwnd_ = nullptr;
		visible_ = false;
		return 0;
	default:
		break;
	}
	return DefWindowProcW(window, message, wParam, lParam);
}


void QuickActionsWindow::ResetPaging(size_t itemsPerPage) noexcept
{
	itemsPerPage_ = (std::max)(size_t{ 1 }, itemsPerPage);
	currentPage_ = 0;
}

void QuickActionsWindow::SetItemsPerPage(size_t itemsPerPage) noexcept
{
	itemsPerPage_ = (std::max)(size_t{ 1 }, itemsPerPage);
	ClampCurrentPage();
}

bool QuickActionsWindow::MovePage(int direction) noexcept
{
	const auto pageCount = PageCount();
	if (pageCount <= 1) return false;
	if (direction < 0)
	{
		currentPage_ = currentPage_ == 0 ? pageCount - 1 : currentPage_ - 1;
	}
	else
	{
		currentPage_ = (currentPage_ + 1) % pageCount;
	}
	return true;
}

size_t QuickActionsWindow::PageCount() const noexcept
{
	return items_.empty() ? 0 : ((items_.size() - 1) / itemsPerPage_) + 1;
}

size_t QuickActionsWindow::CurrentItemCount() const noexcept
{
	const auto firstItem = FirstItemIndex();
	return firstItem >= items_.size()
		? 0
		: (std::min)(itemsPerPage_, items_.size() - firstItem);
}

size_t QuickActionsWindow::FirstItemIndex() const noexcept
{
	return currentPage_ * itemsPerPage_;
}

bool QuickActionsWindow::TryResolveIndex(size_t indexOnPage, size_t& absoluteIndex) const noexcept
{
	if (indexOnPage >= CurrentItemCount()) return false;
	absoluteIndex = FirstItemIndex() + indexOnPage;
	return true;
}

void QuickActionsWindow::ClampCurrentPage() noexcept
{
	const auto pageCount = PageCount();
	currentPage_ = pageCount == 0 ? 0 : (std::min)(currentPage_, pageCount - 1);
}
