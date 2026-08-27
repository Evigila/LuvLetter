#include "windows/MessageQueueWindow.h"

#include "rendering/SurfaceStyleDefaults.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cwctype>
#include <limits>
#include <utility>

using namespace LuvLetterNative;

namespace
{
	constexpr int64_t MaxSurfacePixels = 4LL * 1024LL * 1024LL;
	constexpr size_t MaximumVisibleMessages = 6;
	constexpr float WindowWidthDip = 440.0f;
	constexpr float MessageHeightDip = 32.0f;
	constexpr float HorizontalPaddingDip = 10.0f;
	constexpr float MessageGapDip = 4.0f;
	constexpr float WorkAreaMarginDip = 16.0f;
	constexpr float FontSizeDip = 14.0f;
	constexpr auto MessageLifetime = std::chrono::seconds(5);
	constexpr auto MessageShowDuration = std::chrono::milliseconds(180);
	constexpr auto MessageHideDuration = std::chrono::milliseconds(140);
	constexpr UINT MessageAnimationFrameMs = 16;
	constexpr UINT_PTR MessageTimerId = 1;

	double EaseOutCubic(double progress) noexcept
	{
		progress = (std::clamp)(progress, 0.0, 1.0);
		const auto remaining = 1.0 - progress;
		return 1.0 - (remaining * remaining * remaining);
	}

	struct MessageAnimationFrame final
	{
		float horizontalOffsetDip = 0.0f;
		float opacity = 1.0f;
	};

	MessageAnimationFrame CalculateMessageFrame(
		std::chrono::steady_clock::time_point createdAt,
		std::chrono::steady_clock::time_point expiresAt,
		std::chrono::steady_clock::time_point now) noexcept
	{
		double visibleProgress = 1.0;
		const auto showEnd = createdAt + MessageShowDuration;
		if (now < showEnd)
		{
			visibleProgress = std::chrono::duration<double>(now - createdAt).count()
				/ std::chrono::duration<double>(MessageShowDuration).count();
		}
		else if (now >= expiresAt)
		{
			const auto hideProgress = std::chrono::duration<double>(now - expiresAt).count()
				/ std::chrono::duration<double>(MessageHideDuration).count();
			visibleProgress = 1.0 - hideProgress;
		}

		const auto motionProgress = EaseOutCubic(visibleProgress);
		const auto slideDistance = WindowWidthDip + WorkAreaMarginDip;
		return MessageAnimationFrame{
			-slideDistance * (1.0f - static_cast<float>(motionProgress)),
			static_cast<float>(motionProgress),
		};
	}
}

MessageQueueWindow::MessageQueueWindow(
	ID2D1Factory* d2dFactory,
	IDWriteFactory* dwriteFactory)
	: d2dFactory_(d2dFactory),
	  dwriteFactory_(dwriteFactory),
	  surface_(std::make_unique<LayeredWindowSurface>())
{
}

MessageQueueWindow::~MessageQueueWindow()
{
	StopMessageTimer();
}

HRESULT MessageQueueWindow::Attach(HWND window)
{
	if (window == nullptr || hwnd_ != nullptr) return E_INVALIDARG;
	hwnd_ = window;
	dpi_ = QueryWindowDpi(hwnd_);
	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		0,
		0,
		PixelWidth(),
		PixelHeight(),
		SWP_NOMOVE | SWP_NOACTIVATE);
	SetWindowRgn(hwnd_, nullptr, TRUE);
	return S_OK;
}

void MessageQueueWindow::Enqueue(std::wstring message, HMONITOR targetMonitor)
{
	message = NormalizeMessage(std::move(message));
	if (message.empty()) return;

	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	if (messages_.size() == MaximumMessageCount)
	{
		messages_.pop_front();
	}
	messages_.push_back(QueuedMessage{
		std::move(message),
		now,
		now + MessageLifetime,
	});
	Show(targetMonitor);
}

void MessageQueueWindow::Show(HMONITOR targetMonitor)
{
	if (hwnd_ == nullptr) return;
	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	if (messages_.empty())
	{
		StopMessageTimer();
		Hide();
		return;
	}
	if (targetMonitor != nullptr)
	{
		targetMonitor_ = targetMonitor;
	}

	// Move first so Windows assigns the destination monitor's DPI, then lay out
	// again in DIPs using that DPI. This window never takes focus.
	UpdatePosition();
	RefreshDpiFromWindow();
	visible_ = true;
	UpdateGeometry();
	Render(now);
	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
	SetWindowPos(
		hwnd_, HWND_TOPMOST, 0, 0, 0, 0,
		SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
	ScheduleMessageTimer(now);
}

void MessageQueueWindow::Hide() noexcept
{
	visible_ = false;
	if (hwnd_ != nullptr)
	{
		ShowWindow(hwnd_, SW_HIDE);
	}
	ScheduleMessageTimer(Clock::now());
}

void MessageQueueWindow::Toggle(HMONITOR targetMonitor)
{
	if (visible_)
	{
		Hide();
	}
	else
	{
		Show(targetMonitor);
	}
}

HRESULT MessageQueueWindow::EnsureResources()
{
	if (d2dFactory_ == nullptr || dwriteFactory_ == nullptr) return E_POINTER;

	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	(void)renderScale;

	if (surface_ == nullptr)
	{
		surface_ = std::make_unique<LayeredWindowSurface>();
	}
	auto result = surface_->Ensure(width, height, MaxSurfacePixels);
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

	if (!textFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI",
			nullptr,
			DWRITE_FONT_WEIGHT_NORMAL,
			DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			FontSizeDip,
			L"",
			textFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);

		DWRITE_TRIMMING trimming{};
		trimming.granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER;
		result = dwriteFactory_->CreateEllipsisTrimmingSign(
			textFormat_.Get(),
			trimmingSign_.GetAddressOf());
		if (FAILED(result)) return result;
		result = textFormat_->SetTrimming(&trimming, trimmingSign_.Get());
		if (FAILED(result)) return result;
	}

	if (!backgroundBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(SurfaceBackgroundColor),
			backgroundBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!borderBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(SurfaceBorderColor),
			borderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!textBrush_)
	{
		result = renderTarget_->CreateSolidColorBrush(
			ColorFromArgb(SurfaceContentColor),
			textBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void MessageQueueWindow::DiscardResources(bool discardSurface) noexcept
{
	textBrush_.Reset();
	borderBrush_.Reset();
	backgroundBrush_.Reset();
	trimmingSign_.Reset();
	textFormat_.Reset();
	renderTarget_.Reset();
	if (discardSurface && surface_ != nullptr)
	{
		surface_->Reset();
	}
}

void MessageQueueWindow::RefreshDpiFromWindow()
{
	const auto dpi = QueryWindowDpi(hwnd_);
	if (dpi == dpi_) return;
	dpi_ = dpi;
	DiscardResources(true);
}

void MessageQueueWindow::ApplyDpiChange(UINT dpi, const RECT* suggestedRect)
{
	dpi_ = NormalizeDpi(dpi);
	DiscardResources(true);
	if (suggestedRect != nullptr)
	{
		targetMonitor_ = MonitorFromRect(suggestedRect, MONITOR_DEFAULTTONEAREST);
	}
	UpdateGeometry();
	if (visible_) Render(Clock::now());
}

void MessageQueueWindow::UpdateGeometry()
{
	if (hwnd_ == nullptr) return;
	updatingGeometry_ = true;
	SetWindowRgn(hwnd_, nullptr, TRUE);
	UpdatePosition();
	updatingGeometry_ = false;
}

void MessageQueueWindow::UpdatePosition() const
{
	if (hwnd_ == nullptr) return;
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return;

	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	(void)renderScale;
	const auto margin = (std::max)(0, DipToPixels(WorkAreaMarginDip, dpi_));
	const auto x = monitorInfo.rcWork.left + margin;
	const auto y = (std::max)(
		monitorInfo.rcWork.top,
		monitorInfo.rcWork.bottom - margin - height);
	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		x,
		y,
		width,
		height,
		SWP_NOACTIVATE);
}

void MessageQueueWindow::GetSurfaceMetrics(
	int& width,
	int& height,
	float& renderScale) const
{
	const auto requestedWidth = (std::max)(1, DipToPixels(WindowWidthDip, dpi_));
	const auto requestedHeight = (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
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
		const auto totalMargin = static_cast<int64_t>((std::max)(
			0,
			DipToPixels(WorkAreaMarginDip * 2.0f, dpi_)));
		availableWidth = static_cast<int>((std::max)(int64_t{ 1 }, workWidth - totalMargin));
		availableHeight = static_cast<int>((std::max)(int64_t{ 1 }, workHeight - totalMargin));
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
	if (static_cast<int64_t>(width) > MaxSurfacePixels / static_cast<int64_t>(height))
	{
		width = static_cast<int>(MaxSurfacePixels / static_cast<int64_t>(height));
	}
	renderScale = static_cast<float>((std::min)(
		static_cast<double>(width) / static_cast<double>(requestedWidth),
		static_cast<double>(height) / static_cast<double>(requestedHeight)));
}

float MessageQueueWindow::WindowHeightDip() const noexcept
{
	const auto count = (std::max)(size_t{ 1 }, VisibleMessageCount());
	return static_cast<float>(count) * MessageHeightDip
		+ static_cast<float>(count - 1) * MessageGapDip;
}

size_t MessageQueueWindow::VisibleMessageCount() const noexcept
{
	return (std::min)(messages_.size(), MaximumVisibleMessages);
}

bool MessageQueueWindow::RemoveCompletedMessages(Clock::time_point now)
{
	const auto originalSize = messages_.size();
	while (!messages_.empty()
		&& messages_.front().expiresAt + MessageHideDuration <= now)
	{
		messages_.pop_front();
	}
	return messages_.size() != originalSize;
}

void MessageQueueWindow::ScheduleMessageTimer(Clock::time_point now) noexcept
{
	StopMessageTimer();
	if (hwnd_ == nullptr || messages_.empty()) return;

	bool needsAnimationFrame = false;
	auto nextWake = (Clock::time_point::max)();
	for (const auto& message : messages_)
	{
		const auto showEnd = message.createdAt + MessageShowDuration;
		const auto hideEnd = message.expiresAt + MessageHideDuration;
		if (now < showEnd)
		{
			needsAnimationFrame = needsAnimationFrame || visible_;
			nextWake = (std::min)(nextWake, showEnd);
		}
		else if (now < message.expiresAt)
		{
			nextWake = (std::min)(nextWake, message.expiresAt);
		}
		else if (now < hideEnd)
		{
			needsAnimationFrame = needsAnimationFrame || visible_;
			nextWake = (std::min)(nextWake, hideEnd);
		}
	}

	auto remaining = int64_t{ 1 };
	if (needsAnimationFrame)
	{
		remaining = static_cast<int64_t>(MessageAnimationFrameMs);
	}
	else if (nextWake != (Clock::time_point::max)())
	{
		remaining = std::chrono::ceil<std::chrono::milliseconds>(nextWake - now).count();
	}
	remaining = (std::clamp)(remaining, int64_t{ 1 }, int64_t{ USER_TIMER_MAXIMUM });
	messageTimerActive_ = SetTimer(
		hwnd_,
		MessageTimerId,
		static_cast<UINT>(remaining),
		nullptr) != 0;
}

void MessageQueueWindow::StopMessageTimer() noexcept
{
	if (hwnd_ != nullptr && messageTimerActive_)
	{
		KillTimer(hwnd_, MessageTimerId);
	}
	messageTimerActive_ = false;
}

int MessageQueueWindow::PixelWidth() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	return width;
}

int MessageQueueWindow::PixelHeight() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	return height;
}

void MessageQueueWindow::Render(Clock::time_point now)
{
	if (hwnd_ == nullptr || messages_.empty() || FAILED(EnsureResources())) return;

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

	const auto visibleCount = VisibleMessageCount();
	const auto firstIndex = messages_.size() - visibleCount;
	for (size_t index = 0; index < visibleCount; ++index)
	{
		const auto& queuedMessage = messages_[firstIndex + index];
		const auto animationFrame = CalculateMessageFrame(
			queuedMessage.createdAt,
			queuedMessage.expiresAt,
			now);
		const auto top = static_cast<float>(index) * (MessageHeightDip + MessageGapDip);
		const auto left = animationFrame.horizontalOffsetDip;
		const auto bubble = CreateInsetRoundedRect(
			left,
			top,
			left + WindowWidthDip,
			top + MessageHeightDip,
			SurfaceCornerRadius,
			SurfaceBorderThickness);
		backgroundBrush_->SetOpacity(animationFrame.opacity);
		borderBrush_->SetOpacity(animationFrame.opacity);
		textBrush_->SetOpacity(animationFrame.opacity);
		renderTarget_->FillRoundedRectangle(bubble, backgroundBrush_.Get());
		renderTarget_->DrawRoundedRectangle(
			bubble,
			borderBrush_.Get(),
			SurfaceBorderThickness);

		const auto& message = queuedMessage.text;
		const auto textRect = D2D1::RectF(
			left + HorizontalPaddingDip,
			top,
			left + WindowWidthDip - HorizontalPaddingDip,
			top + MessageHeightDip);
		renderTarget_->DrawTextW(
			message.c_str(),
			static_cast<UINT32>(message.size()),
			textFormat_.Get(),
			textRect,
			textBrush_.Get(),
			D2D1_DRAW_TEXT_OPTIONS_CLIP,
			DWRITE_MEASURING_MODE_NATURAL);
	}
	backgroundBrush_->SetOpacity(1.0f);
	borderBrush_->SetOpacity(1.0f);
	textBrush_->SetOpacity(1.0f);

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

std::wstring MessageQueueWindow::NormalizeMessage(std::wstring message)
{
	for (auto& character : message)
	{
		if (character == L'\r' || character == L'\n' || character == L'\t')
		{
			character = L' ';
		}
	}

	const auto first = std::find_if_not(
		message.begin(),
		message.end(),
		[](wchar_t character) { return std::iswspace(character) != 0; });
	const auto last = std::find_if_not(
		message.rbegin(),
		message.rend(),
		[](wchar_t character) { return std::iswspace(character) != 0; }).base();
	if (first >= last) return {};

	std::wstring normalized(first, last);
	if (normalized.size() > MaximumMessageLength)
	{
		normalized.resize(MaximumMessageLength);
		if (!normalized.empty() && IS_HIGH_SURROGATE(normalized.back()))
		{
			normalized.pop_back();
		}
	}
	return normalized;
}

LRESULT MessageQueueWindow::HandleMessage(
	HWND window,
	UINT message,
	WPARAM wParam,
	LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND:
		return 1;
	case WM_MOUSEACTIVATE:
		return MA_NOACTIVATE;
	case WM_NCHITTEST:
		return HTTRANSPARENT;
	case WM_DPICHANGED:
		ApplyDpiChange(
			static_cast<UINT>(LOWORD(wParam)),
			reinterpret_cast<const RECT*>(lParam));
		return 0;
	case WM_DISPLAYCHANGE:
	case WM_SETTINGCHANGE:
		if (visible_)
		{
			UpdateGeometry();
			Render(Clock::now());
		}
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paint{};
		BeginPaint(window, &paint);
		if (visible_) Render(Clock::now());
		EndPaint(window, &paint);
		return 0;
	}
	case WM_SIZE:
		if (visible_ && !updatingGeometry_) Render(Clock::now());
		return 0;
	case WM_TIMER:
		if (wParam == MessageTimerId)
		{
			StopMessageTimer();
			const auto now = Clock::now();
			const auto changed = RemoveCompletedMessages(now);
			if (messages_.empty())
			{
				Hide();
			}
			else
			{
				if (visible_)
				{
					if (changed) UpdateGeometry();
					Render(now);
				}
				ScheduleMessageTimer(now);
			}
			return 0;
		}
		break;
	case WM_CLOSE:
		Hide();
		return 0;
	case WM_DESTROY:
		StopMessageTimer();
		DiscardResources(true);
		hwnd_ = nullptr;
		visible_ = false;
		return 0;
	default:
		break;
	}
	return DefWindowProcW(window, message, wParam, lParam);
}
