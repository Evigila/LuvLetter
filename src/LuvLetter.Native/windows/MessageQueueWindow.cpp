#include "windows/MessageQueueWindow.h"

#include "rendering/SurfaceStyleDefaults.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cwctype>
#include <utility>

using namespace LuvLetterNative;

namespace
{
	constexpr int64_t MaxSurfacePixels = 4LL * 1024LL * 1024LL;
	constexpr size_t MaximumVisibleMessages = 6;
	constexpr float MaximumMessageWidthDip = 440.0f;
	constexpr float MinimumMessageWidthDip = 32.0f;
	constexpr float MinimumMessageHeightDip = 32.0f;
	constexpr float HorizontalPaddingDip = 10.0f;
	constexpr float VerticalPaddingDip = 6.0f;
	constexpr float MessageGapDip = 4.0f;
	constexpr float WorkAreaMarginDip = 16.0f;
	constexpr float MaximumTextLayoutExtentDip = 1000000.0f;
	constexpr float SpinnerSlotWidthDip = 24.0f;
	constexpr float MinimumTextWidthWithSpinnerDip = 16.0f;
	constexpr float SpinnerRadiusDip = 6.0f;
	constexpr float SpinnerDotRadiusDip = 1.35f;
	constexpr size_t SpinnerDotCount = 8;
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
		std::chrono::steady_clock::time_point now,
		float messageWidthDip) noexcept
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
		const auto slideDistance = messageWidthDip + WorkAreaMarginDip;
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
	  surface_(std::make_unique<LayeredWindowSurface>()),
	  shadowWindow_(d2dFactory)
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
	(void)shadowWindow_.Attach(hwnd_);
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

HRESULT MessageQueueWindow::Enqueue(std::wstring message, HMONITOR targetMonitor)
{
	message = NormalizeMessage(std::move(message));
	if (message.empty()) return S_FALSE;

	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	if (!MakeRoomForMessage())
	{
		return S_FALSE;
	}
	messages_.push_back(MessageQueueEntry{
		0,
		std::move(message),
		now,
		now + MessageLifetime,
		false,
	});
	InvalidateMessageLayouts();
	Show(targetMonitor);
	return S_OK;
}

HRESULT MessageQueueWindow::BeginActivity(
	uint64_t token,
	std::wstring message,
	HMONITOR targetMonitor)
{
	message = NormalizeMessage(std::move(message));
	if (token == 0 || message.empty()) return E_INVALIDARG;

	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	const auto duplicate = std::find_if(
		messages_.begin(),
		messages_.end(),
		[token](const MessageQueueEntry& item) { return item.token == token; });
	if (duplicate != messages_.end()) return E_INVALIDARG;
	if (!MakeRoomForMessage())
	{
		return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA);
	}

	messages_.push_back(MessageQueueEntry{
		token,
		std::move(message),
		now,
		(Clock::time_point::max)(),
		true,
	});
	InvalidateMessageLayouts();
	Show(targetMonitor);
	return S_OK;
}

HRESULT MessageQueueWindow::UpdateActivity(uint64_t token, std::wstring message)
{
	message = NormalizeMessage(std::move(message));
	if (token == 0 || message.empty()) return E_INVALIDARG;

	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	const auto existing = std::find_if(
		messages_.begin(),
		messages_.end(),
		[token](const MessageQueueEntry& item) { return item.token == token; });
	if (existing == messages_.end() || !existing->IsActiveActivity())
	{
		return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
	}

	existing->Update(std::move(message));
	InvalidateMessageLayouts();
	if (visible_)
	{
		UpdateGeometry();
		Render(now);
	}
	ScheduleMessageTimer(now);
	return S_OK;
}

HRESULT MessageQueueWindow::CompleteActivity(
	uint64_t token,
	std::wstring finalMessage,
	bool retainFinalMessage,
	HMONITOR targetMonitor)
{
	if (token == 0) return E_INVALIDARG;
	if (retainFinalMessage)
	{
		finalMessage = NormalizeMessage(std::move(finalMessage));
		retainFinalMessage = !finalMessage.empty();
	}

	const auto now = Clock::now();
	RemoveCompletedMessages(now);
	const auto existing = std::find_if(
		messages_.begin(),
		messages_.end(),
		[token](const MessageQueueEntry& item) { return item.token == token; });
	if (existing == messages_.end() || !existing->IsActiveActivity()) return S_FALSE;

	existing->Complete(std::move(finalMessage), retainFinalMessage, now);
	InvalidateMessageLayouts();
	if (retainFinalMessage)
	{
		Show(targetMonitor);
	}
	else
	{
		if (visible_)
		{
			UpdateGeometry();
			Render(now);
		}
		ScheduleMessageTimer(now);
	}
	return S_OK;
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
		InvalidateMessageLayouts();
	}

	// Move first so Windows assigns the destination monitor's DPI, then lay out
	// again in DIPs using that DPI. This window never takes focus.
	UpdateGeometry();
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
	shadowWindow_.Hide();
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

HRESULT MessageQueueWindow::EnsureTextFormat()
{
	if (dwriteFactory_ == nullptr) return E_POINTER;
	if (textFormat_ && trimmingSign_) return S_OK;
	auto result = S_OK;
	if (!textFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			SurfaceFontFamily,
			nullptr,
			DWRITE_FONT_WEIGHT_NORMAL,
			DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			SurfaceFontSizeDip,
			L"",
			textFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		result = textFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		if (FAILED(result)) return result;
		result = textFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
		if (FAILED(result)) return result;
		result = textFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
		if (FAILED(result)) return result;
	}
	if (!trimmingSign_)
	{
		result = dwriteFactory_->CreateEllipsisTrimmingSign(
			textFormat_.Get(),
			trimmingSign_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	DWRITE_TRIMMING trimming{};
	trimming.granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER;
	return textFormat_->SetTrimming(&trimming, trimmingSign_.Get());
}

HRESULT MessageQueueWindow::EnsureResources()
{
	if (d2dFactory_ == nullptr || dwriteFactory_ == nullptr) return E_POINTER;
	auto result = RebuildMessageLayouts();
	if (FAILED(result)) return result;

	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetSurfaceMetrics(width, height, renderScale);
	(void)renderScale;

	if (surface_ == nullptr)
	{
		surface_ = std::make_unique<LayeredWindowSurface>();
	}
	result = surface_->Ensure(width, height, MaxSurfacePixels);
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

HRESULT MessageQueueWindow::RebuildMessageLayouts()
{
	if (!messageLayoutsDirty_) return S_OK;
	messageLayouts_.clear();
	layoutWidthDip_ = 1.0f;
	layoutHeightDip_ = 1.0f;
	if (messages_.empty())
	{
		messageLayoutsDirty_ = false;
		return S_OK;
	}

	auto result = EnsureTextFormat();
	if (FAILED(result)) return result;
	const auto available = AvailableLayoutSizeDip();
	const auto visibleCount = VisibleMessageCount();
	const auto firstIndex = messages_.size() - visibleCount;
	std::vector<MessageLayout> measured;
	measured.reserve(visibleCount);
	for (size_t visibleIndex = 0; visibleIndex < visibleCount; ++visibleIndex)
	{
		const auto messageIndex = firstIndex + visibleIndex;
		const auto& queuedMessage = messages_[messageIndex];
		const auto showSpinner = queuedMessage.IsActiveActivity()
			&& available.width >= 2.0f * HorizontalPaddingDip
				+ SpinnerSlotWidthDip + MinimumTextWidthWithSpinnerDip;
		const auto spinnerWidth = showSpinner ? SpinnerSlotWidthDip : 0.0f;
		const auto horizontalPadding = (std::min)(
			HorizontalPaddingDip,
			(std::max)(0.0f, (available.width - spinnerWidth - 1.0f) * 0.5f));
		const auto leadingWidth = horizontalPadding + spinnerWidth;
		const auto fixedWidth = leadingWidth + horizontalPadding;

		Microsoft::WRL::ComPtr<IDWriteTextLayout> naturalLayout;
		result = dwriteFactory_->CreateTextLayout(
			queuedMessage.text.c_str(),
			static_cast<UINT32>(queuedMessage.text.size()),
			textFormat_.Get(),
			MaximumTextLayoutExtentDip,
			MaximumTextLayoutExtentDip,
			naturalLayout.GetAddressOf());
		if (FAILED(result)) return result;
		result = naturalLayout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
		if (FAILED(result)) return result;
		DWRITE_TEXT_METRICS naturalMetrics{};
		result = naturalLayout->GetMetrics(&naturalMetrics);
		if (FAILED(result)) return result;

		const auto minimumWidth = (std::min)(MinimumMessageWidthDip, available.width);
		const auto desiredWidth = (std::clamp)(
			naturalMetrics.widthIncludingTrailingWhitespace + fixedWidth,
			minimumWidth,
			available.width);
		const auto textWidth = (std::max)(1.0f, desiredWidth - fixedWidth);
		Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout;
		result = dwriteFactory_->CreateTextLayout(
			queuedMessage.text.c_str(),
			static_cast<UINT32>(queuedMessage.text.size()),
			textFormat_.Get(),
			textWidth,
			(std::max)(1.0f, available.height - 2.0f * VerticalPaddingDip),
			textLayout.GetAddressOf());
		if (FAILED(result)) return result;
		DWRITE_TEXT_METRICS textMetrics{};
		result = textLayout->GetMetrics(&textMetrics);
		if (FAILED(result)) return result;

		MessageLayout layout{};
		layout.messageIndex = messageIndex;
		layout.widthDip = desiredWidth;
		layout.heightDip = (std::min)(
			available.height,
			(std::max)(MinimumMessageHeightDip,
				textMetrics.height + 2.0f * VerticalPaddingDip));
		layout.textLeftDip = leadingWidth;
		layout.showSpinner = showSpinner;
		layout.textLayout = std::move(textLayout);
		measured.push_back(std::move(layout));
	}

	float selectedHeight = 0.0f;
	auto firstVisibleLayout = measured.size();
	while (firstVisibleLayout > 0)
	{
		const auto candidateIndex = firstVisibleLayout - 1;
		const auto additionalHeight = measured[candidateIndex].heightDip
			+ (selectedHeight > 0.0f ? MessageGapDip : 0.0f);
		if (selectedHeight > 0.0f && selectedHeight + additionalHeight > available.height)
		{
			break;
		}
		firstVisibleLayout = candidateIndex;
		selectedHeight = (std::min)(available.height, selectedHeight + additionalHeight);
		if (selectedHeight >= available.height) break;
	}

	messageLayouts_.reserve(measured.size() - firstVisibleLayout);
	float top = 0.0f;
	for (auto index = firstVisibleLayout; index < measured.size(); ++index)
	{
		auto layout = std::move(measured[index]);
		const auto remainingHeight = (std::max)(1.0f, available.height - top);
		layout.heightDip = (std::min)(layout.heightDip, remainingHeight);
		layout.textLayout->SetMaxHeight((std::max)(
			1.0f,
			layout.heightDip - 2.0f * VerticalPaddingDip));
		DWRITE_TEXT_METRICS textMetrics{};
		if (SUCCEEDED(layout.textLayout->GetMetrics(&textMetrics)))
		{
			layout.textTopDip = top + (std::max)(
				0.0f,
				(layout.heightDip - textMetrics.height) * 0.5f);
		}
		else
		{
			layout.textTopDip = top + VerticalPaddingDip;
		}
		layout.topDip = top;
		layoutWidthDip_ = (std::max)(layoutWidthDip_, layout.widthDip);
		top += layout.heightDip;
		messageLayouts_.push_back(std::move(layout));
		if (index + 1 < measured.size()) top += MessageGapDip;
	}
	layoutHeightDip_ = (std::max)(1.0f, top);
	messageLayoutsDirty_ = false;
	return S_OK;
}

void MessageQueueWindow::InvalidateMessageLayouts() noexcept
{
	messageLayoutsDirty_ = true;
	messageLayouts_.clear();
	layoutWidthDip_ = 1.0f;
	layoutHeightDip_ = 1.0f;
}

void MessageQueueWindow::DiscardResources(bool discardSurface) noexcept
{
	InvalidateMessageLayouts();
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
	if (FAILED(RebuildMessageLayouts())) return;
	updatingGeometry_ = true;
	SetWindowRgn(hwnd_, nullptr, TRUE);
	UpdatePosition();
	updatingGeometry_ = false;
}

void MessageQueueWindow::UpdatePosition() const
{
	if (hwnd_ == nullptr) return;
	auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (monitor == nullptr || !GetMonitorInfoW(monitor, &monitorInfo))
	{
		monitor = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
		if (monitor == nullptr || !GetMonitorInfoW(monitor, &monitorInfo)) return;
	}

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

D2D1_SIZE_F MessageQueueWindow::AvailableLayoutSizeDip() const noexcept
{
	auto maximumWidth = MaximumMessageWidthDip;
	auto maximumHeight = MaximumTextLayoutExtentDip;
	auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (hwnd_ != nullptr
			? MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST)
			: MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY));
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	auto hasMonitorInfo = monitor != nullptr && GetMonitorInfoW(monitor, &monitorInfo);
	if (!hasMonitorInfo && hwnd_ != nullptr)
	{
		monitor = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
		hasMonitorInfo = monitor != nullptr && GetMonitorInfoW(monitor, &monitorInfo);
	}
	if (hasMonitorInfo)
	{
		const auto totalMargin = (std::max)(
			0,
			DipToPixels(2.0f * WorkAreaMarginDip, dpi_));
		const auto availableWidth = (std::max)(
			1L,
			monitorInfo.rcWork.right - monitorInfo.rcWork.left - totalMargin);
		const auto availableHeight = (std::max)(
			1L,
			monitorInfo.rcWork.bottom - monitorInfo.rcWork.top - totalMargin);
		maximumWidth = (std::min)(
			maximumWidth,
			PixelsToDip(static_cast<int>(availableWidth), dpi_));
		maximumHeight = PixelsToDip(static_cast<int>(availableHeight), dpi_);
	}

	const auto widthPixels = (std::max)(1, DipToPixels(maximumWidth, dpi_));
	const auto surfaceHeightPixels = static_cast<int>((std::max)(
		int64_t{ 1 },
		MaxSurfacePixels / static_cast<int64_t>(widthPixels)));
	maximumHeight = (std::min)(
		maximumHeight,
		PixelsToDip(surfaceHeightPixels, dpi_));
	return D2D1::SizeF(
		(std::max)(1.0f, maximumWidth),
		(std::max)(1.0f, maximumHeight));
}

void MessageQueueWindow::GetSurfaceMetrics(
	int& width,
	int& height,
	float& renderScale) const
{
	width = (std::max)(1, DipToPixels(WindowWidthDip(), dpi_));
	height = (std::max)(1, DipToPixels(WindowHeightDip(), dpi_));
	if (static_cast<int64_t>(height) > MaxSurfacePixels / static_cast<int64_t>(width))
	{
		height = static_cast<int>((std::max)(
			int64_t{ 1 },
			MaxSurfacePixels / static_cast<int64_t>(width)));
	}
	renderScale = 1.0f;
}

float MessageQueueWindow::WindowWidthDip() const noexcept
{
	return (std::max)(1.0f, layoutWidthDip_);
}

float MessageQueueWindow::WindowHeightDip() const noexcept
{
	return (std::max)(1.0f, layoutHeightDip_);
}

size_t MessageQueueWindow::VisibleMessageCount() const noexcept
{
	return (std::min)(messages_.size(), MaximumVisibleMessages);
}

bool MessageQueueWindow::RemoveCompletedMessages(Clock::time_point now)
{
	const auto originalSize = messages_.size();
	for (auto message = messages_.begin(); message != messages_.end();)
	{
		if (message->IsRemovalDue(now))
		{
			message = messages_.erase(message);
		}
		else
		{
			++message;
		}
	}
	const auto changed = messages_.size() != originalSize;
	if (changed) InvalidateMessageLayouts();
	return changed;
}

bool MessageQueueWindow::MakeRoomForMessage() noexcept
{
	if (messages_.size() < MaximumMessageCount) return true;
	const auto transient = std::find_if(
		messages_.begin(),
		messages_.end(),
		[](const MessageQueueEntry& item) { return !item.IsActiveActivity(); });
	if (transient == messages_.end()) return false;
	messages_.erase(transient);
	InvalidateMessageLayouts();
	return true;
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
		if (visible_ && (now < showEnd || message.IsActiveActivity()))
		{
			needsAnimationFrame = true;
		}
		if (!message.HasFiniteLifetime())
		{
			continue;
		}

		const auto hideEnd = message.expiresAt + MessageHideDuration;
		if (now < message.expiresAt)
		{
			nextWake = (std::min)(nextWake, message.expiresAt);
		}
		else if (now < hideEnd)
		{
			needsAnimationFrame = needsAnimationFrame || visible_;
			nextWake = (std::min)(nextWake, hideEnd);
		}
	}

	if (!needsAnimationFrame && nextWake == (Clock::time_point::max)()) return;

	auto remaining = int64_t{ MessageAnimationFrameMs };
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

	std::vector<LuvLetterNative::SurfaceShadowShape> shadowShapes;
	shadowShapes.reserve(messageLayouts_.size());
	for (const auto& layout : messageLayouts_)
	{
		if (layout.messageIndex >= messages_.size()) continue;
		const auto& queuedMessage = messages_[layout.messageIndex];
		const auto animationFrame = CalculateMessageFrame(
			queuedMessage.createdAt,
			queuedMessage.expiresAt,
			now,
			layout.widthDip);
		const auto top = layout.topDip;
		const auto left = animationFrame.horizontalOffsetDip;
		shadowShapes.push_back(LuvLetterNative::SurfaceShadowShape{
			D2D1::RectF(
				left,
				top,
				left + layout.widthDip,
				top + layout.heightDip),
			SurfaceCornerRadius,
			animationFrame.opacity,
		});
		const auto bubble = CreateInsetRoundedRect(
			left,
			top,
			left + layout.widthDip,
			top + layout.heightDip,
			SurfaceCornerRadius,
			SurfaceBorderThickness);
		backgroundBrush_->SetOpacity(animationFrame.opacity);
		borderBrush_->SetOpacity(animationFrame.opacity);
		renderTarget_->FillRoundedRectangle(bubble, backgroundBrush_.Get());
		renderTarget_->DrawRoundedRectangle(
			bubble,
			borderBrush_.Get(),
			SurfaceBorderThickness);

		if (queuedMessage.IsActiveActivity() && layout.showSpinner)
		{
			constexpr double TwoPi = 6.283185307179586476925286766559;
			const auto phase = CalculateMessageSpinnerRadians(
				queuedMessage.createdAt,
				now);
			const auto centerX = left + HorizontalPaddingDip + SpinnerSlotWidthDip * 0.42f;
			const auto centerY = top + layout.heightDip * 0.5f;
			for (size_t dot = 0; dot < SpinnerDotCount; ++dot)
			{
				const auto angle = phase
					- static_cast<double>(dot) * TwoPi / static_cast<double>(SpinnerDotCount);
				const auto x = centerX + SpinnerRadiusDip * static_cast<float>(std::cos(angle));
				const auto y = centerY + SpinnerRadiusDip * static_cast<float>(std::sin(angle));
				const auto trailOpacity = 1.0f
					- static_cast<float>(dot) / static_cast<float>(SpinnerDotCount) * 0.78f;
				textBrush_->SetOpacity(animationFrame.opacity * trailOpacity);
				renderTarget_->FillEllipse(
					D2D1::Ellipse(D2D1::Point2F(x, y), SpinnerDotRadiusDip, SpinnerDotRadiusDip),
					textBrush_.Get());
			}
		}

		textBrush_->SetOpacity(animationFrame.opacity);
		renderTarget_->DrawTextLayout(
			D2D1::Point2F(
				left + layout.textLeftDip,
				layout.textTopDip),
			layout.textLayout.Get(),
			textBrush_.Get(),
			D2D1_DRAW_TEXT_OPTIONS_CLIP);
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
		if (surface_->Present(hwnd_, width, height))
		{
			(void)shadowWindow_.Update(dpi_, shadowShapes);
		}
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
		InvalidateMessageLayouts();
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
