#include "host/OverlayHost.h"

#include "interop/OverlayRequest.h"

#include <string>
#include <type_traits>
#include <utility>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "windowscodecs.lib")

namespace
{
	constexpr wchar_t WindowClassName[] = L"LuvLetter.Core.OverlayWindow";
	constexpr UINT_PTR AnimationTimerId = 1;

	std::wstring CopyTextValue(const wchar_t* text, int32_t textLength)
	{
		if (text == nullptr || textLength <= 0)
		{
			return {};
		}

		return std::wstring(text, text + textLength);
	}
}

OverlayHost& OverlayHost::Instance()
{
	static OverlayHost instance;
	return instance;
}

HRESULT OverlayHost::Start(const LuvLetterOverlayStartOptions& options)
{
	if (running_)
	{
		return S_FALSE;
	}

	if (threadHandle_ != nullptr)
	{
		return S_FALSE;
	}

	if (options.logoData == nullptr || options.logoSize <= 0)
	{
		return E_INVALIDARG;
	}

	stateStore_.Initialize(options);

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

HRESULT OverlayHost::UpdateLayout(const LuvLetterOverlayLayoutConfig& layoutConfig)
{
	if (!requestDispatcher_.Enqueue(OverlayLayoutUpdateRequest{ layoutConfig }))
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	return S_OK;
}

HRESULT OverlayHost::UpdateLogo(const uint8_t* logoData, size_t logoSize)
{
	if (logoData == nullptr || logoSize == 0)
	{
		return E_INVALIDARG;
	}

	OverlayLogoUpdateRequest request{};
	request.logoBytes.assign(logoData, logoData + logoSize);
	if (!requestDispatcher_.Enqueue(std::move(request)))
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	return S_OK;
}

HRESULT OverlayHost::UpdateText(const wchar_t* text, int32_t textLength)
{
	return UpdateOutputText(text, textLength);
}

HRESULT OverlayHost::UpdateInputText(const wchar_t* text, int32_t textLength)
{
	OverlayInputTextUpdateRequest request{};
	request.text = CopyTextValue(text, textLength);
	if (!requestDispatcher_.Enqueue(std::move(request)))
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	return S_OK;
}

HRESULT OverlayHost::UpdateOutputText(const wchar_t* text, int32_t textLength)
{
	OverlayOutputTextUpdateRequest request{};
	request.text = CopyTextValue(text, textLength);
	if (!requestDispatcher_.Enqueue(std::move(request)))
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	return S_OK;
}

HRESULT OverlayHost::SetVisualMode(LuvLetterOverlayVisualMode visualMode)
{
	if (visualMode != LuvLetterOverlayVisualMode_Badge &&
		visualMode != LuvLetterOverlayVisualMode_CommandLine)
	{
		return E_INVALIDARG;
	}

	if (!requestDispatcher_.Enqueue(OverlayVisualModeUpdateRequest{ visualMode }))
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	return S_OK;
}

void OverlayHost::SetEventCallback(LuvLetterOverlayEventCallback callback, void* context)
{
	eventBridge_.SetCallback(callback, context);
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

	const auto initializeRendererResult = renderer_.Initialize();
	if (FAILED(initializeRendererResult))
	{
		if (needsUninitialize)
		{
			CoUninitialize();
		}

		return initializeRendererResult;
	}

	const auto createWindowResult = CreateOverlayWindow();
	if (FAILED(createWindowResult))
	{
		renderer_.DiscardDeviceResources();
		stateStore_.Reset();
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

	if (hwnd_ != nullptr)
	{
		DestroyWindow(hwnd_);
		hwnd_ = nullptr;
	}

	requestDispatcher_.UnbindWindow();
	renderer_.DiscardDeviceResources();
	stateStore_.Reset();
	animationTargets_.clear();
	finalVisualModeAfterAnimation_.reset();
	running_ = false;

	if (needsUninitialize)
	{
		CoUninitialize();
	}

	return S_OK;
}

HRESULT OverlayHost::CreateOverlayWindow()
{
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

	MONITORINFO monitorInfo{};
	if (!TryGetAnchorMonitorInfo(monitorInfo))
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	const RECT seedWindowRect{
		0,
		0,
		stateStore_.Snapshot().layoutConfig.overlayWidth,
		stateStore_.Snapshot().layoutConfig.overlayHeight,
	};
	layoutSnapshot_ = layoutEngine_.Compute(stateStore_.Snapshot(), monitorInfo, seedWindowRect);
	const auto windowWidth =
		layoutSnapshot_.badgeHiddenWindowRect.right - layoutSnapshot_.badgeHiddenWindowRect.left;
	const auto windowHeight =
		layoutSnapshot_.badgeHiddenWindowRect.bottom - layoutSnapshot_.badgeHiddenWindowRect.top;

	hwnd_ = CreateWindowExW(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
		WindowClassName,
		L"LuvLetter Overlay",
		WS_POPUP,
		layoutSnapshot_.badgeHiddenWindowRect.left,
		layoutSnapshot_.badgeHiddenWindowRect.top,
		windowWidth,
		windowHeight,
		nullptr,
		nullptr,
		GetModuleHandleW(nullptr),
		this);

	if (hwnd_ == nullptr)
	{
		return HRESULT_FROM_WIN32(GetLastError());
	}

	lastAppliedWindowRect_ = layoutSnapshot_.badgeHiddenWindowRect;
	requestDispatcher_.BindWindow(hwnd_);
	ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
	UpdateWindow(hwnd_);
	RefreshLayout(true);
	InvalidateRect(hwnd_, nullptr, FALSE);

	return S_OK;
}

bool OverlayHost::TryGetAnchorMonitorInfo(MONITORINFO& monitorInfo) const
{
	POINT anchorPoint{ 0, GetSystemMetrics(SM_CYSCREEN) - 1 };
	const auto monitor = MonitorFromPoint(anchorPoint, MONITOR_DEFAULTTOPRIMARY);
	monitorInfo = {};
	monitorInfo.cbSize = sizeof(monitorInfo);
	return GetMonitorInfoW(monitor, &monitorInfo) != FALSE;
}

void OverlayHost::RefreshLayout(bool playEntranceAnimation)
{
	RECT currentWindowRect = GetCurrentWindowRect();
	UpdateLayoutSnapshotForRect(currentWindowRect);
	if (hwnd_ == nullptr)
	{
		return;
	}

	if (playEntranceAnimation)
	{
		StartAnimationSequence(
			layoutSnapshot_.badgeHiddenWindowRect,
			{ layoutSnapshot_.badgeVisibleWindowRect },
			std::nullopt);
		return;
	}

	KillTimer(hwnd_, AnimationTimerId);
	animationTargets_.clear();
	finalVisualModeAfterAnimation_.reset();

	const auto targetRect = stateStore_.Snapshot().visualMode == LuvLetterOverlayVisualMode_CommandLine
		? layoutSnapshot_.commandVisibleWindowRect
		: layoutSnapshot_.badgeVisibleWindowRect;
	animationSystem_.Start(targetRect, targetRect, GetTickCount64(), 0);
	ApplyWindowRect(targetRect);
	UpdateLayoutSnapshotForRect(targetRect);
}

void OverlayHost::TransitionToVisualMode(LuvLetterOverlayVisualMode visualMode)
{
	RECT currentWindowRect = GetCurrentWindowRect();
	UpdateLayoutSnapshotForRect(currentWindowRect);

	const auto currentMode = stateStore_.Snapshot().visualMode;
	const auto targetRect = visualMode == LuvLetterOverlayVisualMode_CommandLine
		? layoutSnapshot_.commandVisibleWindowRect
		: layoutSnapshot_.badgeVisibleWindowRect;
	if (currentMode == visualMode && !animationSystem_.IsActive() && EqualRect(&currentWindowRect, &targetRect))
	{
		return;
	}

	if (visualMode == LuvLetterOverlayVisualMode_CommandLine)
	{
		stateStore_.SetVisualMode(LuvLetterOverlayVisualMode_CommandLine);
		UpdateLayoutSnapshotForRect(currentWindowRect);
		StartAnimationSequence(
			currentWindowRect,
			{ layoutSnapshot_.commandBarWindowRect, layoutSnapshot_.commandVisibleWindowRect },
			std::nullopt);
		return;
	}

	StartAnimationSequence(
		currentWindowRect,
		{ layoutSnapshot_.commandBarWindowRect, layoutSnapshot_.badgeVisibleWindowRect },
		LuvLetterOverlayVisualMode_Badge);
}

void OverlayHost::ApplyWindowRect(const RECT& windowRect) const
{
	lastAppliedWindowRect_ = windowRect;

	if (hwnd_ == nullptr)
	{
		return;
	}

	SetWindowPos(
		hwnd_,
		HWND_TOPMOST,
		windowRect.left,
		windowRect.top,
		windowRect.right - windowRect.left,
		windowRect.bottom - windowRect.top,
		SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

void OverlayHost::AdvanceAnimation()
{
	if (!animationSystem_.IsActive())
	{
		KillTimer(hwnd_, AnimationTimerId);
		return;
	}

	const auto animationStep = animationSystem_.Advance(GetTickCount64());
	ApplyWindowRect(animationStep.windowRect);
	UpdateLayoutSnapshotForRect(animationStep.windowRect);
	InvalidateRect(hwnd_, nullptr, FALSE);
	if (!animationStep.completed)
	{
		return;
	}

	if (!animationTargets_.empty())
	{
		BeginNextAnimationPhase(animationStep.windowRect);
		return;
	}

	KillTimer(hwnd_, AnimationTimerId);
	if (finalVisualModeAfterAnimation_.has_value())
	{
		stateStore_.SetVisualMode(*finalVisualModeAfterAnimation_);
		finalVisualModeAfterAnimation_.reset();
		UpdateLayoutSnapshotForRect(animationStep.windowRect);
		InvalidateRect(hwnd_, nullptr, FALSE);
	}
}

void OverlayHost::HandleQueuedRequests()
{
	const auto requests = requestDispatcher_.Drain();
	if (requests.empty())
	{
		return;
	}

	bool layoutChanged = false;
	bool shouldRedraw = false;
	std::optional<LuvLetterOverlayVisualMode> requestedVisualMode;

	for (const auto& request : requests)
	{
		std::visit(
			[&](const auto& typedRequest)
			{
				using RequestType = std::decay_t<decltype(typedRequest)>;
				if constexpr (std::is_same_v<RequestType, OverlayLayoutUpdateRequest>)
				{
					stateStore_.UpdateLayout(typedRequest.layoutConfig);
					layoutChanged = true;
					shouldRedraw = true;
				}
				else if constexpr (std::is_same_v<RequestType, OverlayLogoUpdateRequest>)
				{
					stateStore_.UpdateLogo(typedRequest.logoBytes.data(), typedRequest.logoBytes.size());
					renderer_.ResetLogoBitmap();
					shouldRedraw = true;
				}
				else if constexpr (std::is_same_v<RequestType, OverlayTextUpdateRequest>)
				{
					stateStore_.UpdateOutputText(typedRequest.text);
					shouldRedraw = true;
				}
				else if constexpr (std::is_same_v<RequestType, OverlayInputTextUpdateRequest>)
				{
					stateStore_.UpdateInputText(typedRequest.text);
					shouldRedraw = true;
				}
				else if constexpr (std::is_same_v<RequestType, OverlayOutputTextUpdateRequest>)
				{
					stateStore_.UpdateOutputText(typedRequest.text);
					shouldRedraw = true;
				}
				else if constexpr (std::is_same_v<RequestType, OverlayVisualModeUpdateRequest>)
				{
					requestedVisualMode = typedRequest.visualMode;
					shouldRedraw = true;
				}
			},
			request);
	}

	if (requestedVisualMode.has_value())
	{
		TransitionToVisualMode(*requestedVisualMode);
	}
	else if (layoutChanged)
	{
		RefreshLayout(false);
	}

	if (shouldRedraw && hwnd_ != nullptr)
	{
		InvalidateRect(hwnd_, nullptr, FALSE);
	}
}

void OverlayHost::UpdateLayoutSnapshotForRect(const RECT& windowRect)
{
	MONITORINFO monitorInfo{};
	if (!TryGetAnchorMonitorInfo(monitorInfo))
	{
		return;
	}

	layoutSnapshot_ = layoutEngine_.Compute(stateStore_.Snapshot(), monitorInfo, windowRect);
}

void OverlayHost::StartAnimationSequence(
	const RECT& fromRect,
	std::initializer_list<RECT> targetRects,
	std::optional<LuvLetterOverlayVisualMode> finalVisualMode)
{
	KillTimer(hwnd_, AnimationTimerId);
	animationTargets_.assign(targetRects.begin(), targetRects.end());
	finalVisualModeAfterAnimation_ = finalVisualMode;
	BeginNextAnimationPhase(fromRect);
}

void OverlayHost::BeginNextAnimationPhase(const RECT& fromRect)
{
	if (animationTargets_.empty())
	{
		ApplyWindowRect(fromRect);
		UpdateLayoutSnapshotForRect(fromRect);
		InvalidateRect(hwnd_, nullptr, FALSE);
		if (finalVisualModeAfterAnimation_.has_value())
		{
			stateStore_.SetVisualMode(*finalVisualModeAfterAnimation_);
			finalVisualModeAfterAnimation_.reset();
			UpdateLayoutSnapshotForRect(fromRect);
		}

		return;
	}

	const auto targetRect = animationTargets_.front();
	animationTargets_.pop_front();
	animationSystem_.Start(
		fromRect,
		targetRect,
		GetTickCount64(),
		stateStore_.Snapshot().layoutConfig.animationDurationMs);
	ApplyWindowRect(fromRect);
	UpdateLayoutSnapshotForRect(fromRect);
	InvalidateRect(hwnd_, nullptr, FALSE);

	if (!animationSystem_.IsActive())
	{
		ApplyWindowRect(targetRect);
		UpdateLayoutSnapshotForRect(targetRect);
		BeginNextAnimationPhase(targetRect);
		return;
	}

	SetTimer(hwnd_, AnimationTimerId, 16, nullptr);
}

RECT OverlayHost::GetCurrentWindowRect() const
{
	if (hwnd_ == nullptr)
	{
		return lastAppliedWindowRect_;
	}

	RECT windowRect{};
	if (GetWindowRect(hwnd_, &windowRect) == 0)
	{
		return lastAppliedWindowRect_;
	}

	return windowRect;
}

LRESULT OverlayHost::HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND:
		return 1;
	case WM_NCHITTEST:
		return HTTRANSPARENT;
	case WM_PAINT:
	{
		PAINTSTRUCT paintStruct{};
		BeginPaint(hwnd, &paintStruct);
		renderer_.Render(hwnd, stateStore_.Snapshot(), layoutSnapshot_);
		EndPaint(hwnd, &paintStruct);
		return 0;
	}
	case OverlayRequestDispatcher::RequestMessageId:
		HandleQueuedRequests();
		return 0;
	case WM_TIMER:
		if (wParam == AnimationTimerId)
		{
			AdvanceAnimation();
			return 0;
		}

		break;
	case WM_DISPLAYCHANGE:
		RefreshLayout(false);
		InvalidateRect(hwnd, nullptr, FALSE);
		return 0;
	case WM_SIZE:
		renderer_.Resize(LOWORD(lParam), HIWORD(lParam));
		UpdateLayoutSnapshotForRect(GetCurrentWindowRect());
		return 0;
	case WM_CLOSE:
		DestroyWindow(hwnd);
		return 0;
	case WM_DESTROY:
		KillTimer(hwnd, AnimationTimerId);
		animationTargets_.clear();
		finalVisualModeAfterAnimation_.reset();
		requestDispatcher_.UnbindWindow();
		hwnd_ = nullptr;
		PostQuitMessage(0);
		return 0;
	default:
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT CALLBACK OverlayHost::WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_NCCREATE)
	{
		const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
		auto* host = static_cast<OverlayHost*>(createStruct->lpCreateParams);
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
		return TRUE;
	}

	auto* host = reinterpret_cast<OverlayHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
	if (host == nullptr)
	{
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	return host->HandleMessage(hwnd, message, wParam, lParam);
}
