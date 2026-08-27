#include "host/NativeShellHost.h"

#include "rendering/LayeredWindowSurface.h"
#include "windows/QuickActionsWindow.h"
#include "windows/InputWindow.h"

#include <Windows.h>

#include <algorithm>
#include <atomic>
#include <limits>
#include <new>
#include <utility>
#include <vector>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")

using namespace LuvLetterNative;

namespace
{
	constexpr wchar_t InputWindowClassName[] = L"LuvLetter.Native.InputBox";
	constexpr wchar_t QuickActionsWindowClassName[] = L"LuvLetter.Native.QuickActionsWindow";
	constexpr UINT HostRequestMessage = WM_APP + 40;
	constexpr UINT HostShutdownMessage = WM_APP + 41;
	constexpr DWORD StartupTimeoutMs = 10000;
	constexpr DWORD RequestTimeoutMs = 5000;
	constexpr DWORD ShutdownTimeoutMs = 5000;
	constexpr int32_t MaxQuickActions = 4096;

	enum class RequestKind
	{
		ApplyInputConfig,
		SetInputCallback,
		ShowInput,
		HideInput,
		ToggleInput,
		ApplyQuickActionsConfig,
		SetQuickActions,
		SetQuickActionCallback,
		ShowQuickActions,
		HideQuickActions,
		ToggleQuickActions,
		HidePopups,
	};
}

struct NativeShellHost::HostRequest
{
	enum class ExecutionState : long
	{
		Pending,
		Executing,
		Canceled,
		Completed,
	};

	explicit HostRequest(RequestKind requestKind, bool createCompletionEvent)
		: kind(requestKind),
		completed(createCompletionEvent ? CreateEventW(nullptr, TRUE, FALSE, nullptr) : nullptr)
	{
	}

	~HostRequest()
	{
		if (completed != nullptr)
		{
			CloseHandle(completed);
		}
	}

	void AddRef() noexcept
	{
		refs.fetch_add(1, std::memory_order_relaxed);
	}

	void Release() noexcept
	{
		if (refs.fetch_sub(1, std::memory_order_acq_rel) == 1)
		{
			delete this;
		}
	}

	bool TryBeginExecution() noexcept
	{
		auto expected = static_cast<long>(ExecutionState::Pending);
		return executionState.compare_exchange_strong(
			expected,
			static_cast<long>(ExecutionState::Executing),
			std::memory_order_acq_rel);
	}

	bool TryCancelPending() noexcept
	{
		auto expected = static_cast<long>(ExecutionState::Pending);
		return executionState.compare_exchange_strong(
			expected,
			static_cast<long>(ExecutionState::Canceled),
			std::memory_order_acq_rel);
	}

	void MarkCompleted() noexcept
	{
		executionState.store(
			static_cast<long>(ExecutionState::Completed),
			std::memory_order_release);
	}

	std::atomic<long> refs{ 1 };
	std::atomic<long> executionState{ static_cast<long>(ExecutionState::Pending) };
	RequestKind kind;
	HANDLE completed = nullptr;
	HRESULT result = E_PENDING;
	LuvLetterInputBoxConfig inputConfig{};
	LuvLetterFeatureWindowConfig quickActionsConfig{};
	std::vector<QuickActionItem> quickActions;
	LuvLetterInputSubmittedCallback inputCallback = nullptr;
	LuvLetterFeatureActivatedCallback quickActionCallback = nullptr;
	void* callbackContext = nullptr;
};

NativeShellHost& NativeShellHost::Instance()
{
	static NativeShellHost instance;
	return instance;
}

HRESULT NativeShellHost::ApplyConfig(const LuvLetterInputBoxConfig& config)
{
	if (config.structSize != sizeof(LuvLetterInputBoxConfig)
		|| config.abiVersion != LUVLETTER_NATIVE_ABI_VERSION)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::ApplyInputConfig, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->inputConfig = config;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetInputSubmittedCallback(
	LuvLetterInputSubmittedCallback callback,
	void* context)
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::SetInputCallback, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->inputCallback = callback;
	request->callbackContext = context;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::Show()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::Hide()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::Toggle()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::ApplyQuickActionsConfig(const LuvLetterFeatureWindowConfig& config)
{
	if (config.structSize != sizeof(LuvLetterFeatureWindowConfig)
		|| config.abiVersion != LUVLETTER_NATIVE_ABI_VERSION)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::ApplyQuickActionsConfig, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->quickActionsConfig = config;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetQuickActions(const LuvLetterFeatureItem* items, int32_t count)
{
	if (count < 0 || count > MaxQuickActions || (count > 0 && items == nullptr))
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::SetQuickActions, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}

	try
	{
		request->quickActions.reserve(static_cast<size_t>(count));
		for (int32_t index = 0; index < count; ++index)
		{
			QuickActionItem item{};
			item.token = items[index].token;
			if (items[index].label != nullptr)
			{
				item.label.assign(items[index].label, wcsnlen_s(items[index].label, 256));
			}
			request->quickActions.push_back(std::move(item));
		}
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}

	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetQuickActionActivatedCallback(
	LuvLetterFeatureActivatedCallback callback,
	void* context)
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::SetQuickActionCallback, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->quickActionCallback = callback;
	request->callbackContext = context;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::ShowQuickActionsWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowQuickActions, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::HideQuickActionsWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideQuickActions, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::ToggleQuickActionsWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleQuickActions, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::HidePopups()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HidePopups, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::EnsureThread()
{
	std::lock_guard lock(lifecycleMutex_);
	return EnsureThreadLocked();
}

HRESULT NativeShellHost::EnsureThreadLocked()
{
	if (threadHandle_ != nullptr)
	{
		const auto threadState = WaitForSingleObject(threadHandle_, 0);
		if (threadState == WAIT_OBJECT_0)
		{
			CloseHandle(threadHandle_);
			threadHandle_ = nullptr;
			threadId_ = 0;
			stopping_ = false;
			if (startedEvent_ != nullptr)
			{
				CloseHandle(startedEvent_);
				startedEvent_ = nullptr;
			}
		}
		else if (threadState == WAIT_FAILED)
		{
			return LastErrorAsHresult();
		}
		else if (stopping_)
		{
			return HRESULT_FROM_WIN32(ERROR_SHUTDOWN_IN_PROGRESS);
		}
		else if (startedEvent_ == nullptr)
		{
			return S_OK;
		}
		else
		{
			const auto startupWait = WaitForSingleObject(startedEvent_, StartupTimeoutMs);
			if (startupWait == WAIT_TIMEOUT)
			{
				return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
			}
			if (startupWait != WAIT_OBJECT_0)
			{
				return LastErrorAsHresult();
			}

			CloseHandle(startedEvent_);
			startedEvent_ = nullptr;
			return startResult_;
		}
	}

	startedEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (startedEvent_ == nullptr)
	{
		return LastErrorAsHresult();
	}

	startResult_ = E_PENDING;
	stopping_ = false;
	threadHandle_ = CreateThread(nullptr, 0, &NativeShellHost::ThreadEntry, this, 0, &threadId_);
	if (threadHandle_ == nullptr)
	{
		const auto hr = LastErrorAsHresult();
		CloseHandle(startedEvent_);
		startedEvent_ = nullptr;
		threadId_ = 0;
		return hr;
	}

	const auto startupWait = WaitForSingleObject(startedEvent_, StartupTimeoutMs);
	if (startupWait == WAIT_TIMEOUT)
	{
		return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
	}
	if (startupWait != WAIT_OBJECT_0)
	{
		return LastErrorAsHresult();
	}

	CloseHandle(startedEvent_);
	startedEvent_ = nullptr;
	if (FAILED(startResult_))
	{
		const auto shutdownWait = WaitForSingleObject(threadHandle_, ShutdownTimeoutMs);
		if (shutdownWait == WAIT_OBJECT_0)
		{
			CloseHandle(threadHandle_);
			threadHandle_ = nullptr;
			threadId_ = 0;
			stopping_ = false;
		}
		else
		{
			// Preserve the live handle so a later caller cannot start a second UI thread.
			stopping_ = true;
		}
	}
	return startResult_;
}

HRESULT NativeShellHost::DispatchRequest(HostRequest* request, bool waitForCompletion)
{
	if (request == nullptr)
	{
		return E_INVALIDARG;
	}
	if (waitForCompletion && request->completed == nullptr)
	{
		request->Release();
		return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY);
	}

	const auto ensureResult = EnsureThread();
	if (FAILED(ensureResult))
	{
		request->Release();
		return ensureResult;
	}

	bool executeInline = false;
	HRESULT postResult = S_OK;
	{
		std::lock_guard lock(lifecycleMutex_);
		if (stopping_)
		{
			postResult = HRESULT_FROM_WIN32(ERROR_SHUTDOWN_IN_PROGRESS);
		}
		else if (threadHandle_ == nullptr || threadId_ == 0
			|| WaitForSingleObject(threadHandle_, 0) != WAIT_TIMEOUT)
		{
			postResult = HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
		}
		else if (GetCurrentThreadId() == threadId_)
		{
			executeInline = true;
		}
		else
		{
			request->AddRef();
			if (!PostThreadMessageW(threadId_, HostRequestMessage, 0, reinterpret_cast<LPARAM>(request)))
			{
				postResult = LastErrorAsHresult();
				request->Release();
			}
		}
	}

	if (FAILED(postResult))
	{
		request->Release();
		return postResult;
	}

	if (executeInline)
	{
		HRESULT result = HRESULT_FROM_WIN32(ERROR_CANCELLED);
		if (request->TryBeginExecution())
		{
			try
			{
				result = ProcessRequest(*request);
			}
			catch (...)
			{
				result = E_FAIL;
			}
		}
		CompleteRequest(request, result);
		return result;
	}

	HRESULT result = S_OK;
	if (waitForCompletion)
	{
		const auto waitResult = WaitForSingleObject(request->completed, RequestTimeoutMs);
		if (waitResult == WAIT_OBJECT_0)
		{
			result = request->result;
		}
		else if (waitResult == WAIT_TIMEOUT)
		{
			if (request->TryCancelPending())
			{
				result = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
			}
			else
			{
				// Execution already began. Waiting preserves the synchronous ABI contract:
				// callers may release callback/context memory as soon as this call returns.
				const auto completionWait = WaitForSingleObject(request->completed, INFINITE);
				result = completionWait == WAIT_OBJECT_0
					? request->result
					: LastErrorAsHresult();
			}
		}
		else
		{
			result = LastErrorAsHresult();
		}
	}

	request->Release();
	return result;
}

void NativeShellHost::CompleteRequest(HostRequest* request, HRESULT result) noexcept
{
	if (request == nullptr)
	{
		return;
	}
	request->result = result;
	request->MarkCompleted();
	if (request->completed != nullptr)
	{
		SetEvent(request->completed);
	}
	request->Release();
}

HRESULT NativeShellHost::ProcessRequest(HostRequest& request)
{
	if (inputWindow_ == nullptr || quickActionsWindow_ == nullptr)
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	switch (request.kind)
	{
	case RequestKind::ApplyInputConfig:
		inputWindow_->ApplyConfiguration(request.inputConfig);
		return S_OK;
	case RequestKind::SetInputCallback:
		inputSubmittedCallback_ = request.inputCallback;
		inputSubmittedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::ShowInput:
	{
		CapturePreviousForegroundWindow();
		const auto monitor = CaptureTargetMonitor();
		inputWindow_->Show(monitor, previousForegroundHwnd_);
		return S_OK;
	}
	case RequestKind::HideInput:
		inputWindow_->Hide();
		return S_OK;
	case RequestKind::ToggleInput:
		if (inputWindow_->IsVisible())
		{
			inputWindow_->Hide();
		}
		else
		{
			CapturePreviousForegroundWindow();
			const auto monitor = CaptureTargetMonitor();
			inputWindow_->Show(monitor, previousForegroundHwnd_);
		}
		return S_OK;
	case RequestKind::ApplyQuickActionsConfig:
		quickActionsWindow_->ApplyConfiguration(request.quickActionsConfig);
		return S_OK;
	case RequestKind::SetQuickActions:
		quickActionsWindow_->SetItems(std::move(request.quickActions));
		return S_OK;
	case RequestKind::SetQuickActionCallback:
		quickActionActivatedCallback_ = request.quickActionCallback;
		quickActionActivatedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::ShowQuickActions:
		if (quickActionsWindow_->IsEmpty()) return S_FALSE;
		CapturePreviousForegroundWindow();
		{
			const auto monitor = CaptureTargetMonitor();
			quickActionsWindow_->Show(monitor, previousForegroundHwnd_);
		}
		return S_OK;
	case RequestKind::HideQuickActions:
		quickActionsWindow_->Hide();
		return S_OK;
	case RequestKind::ToggleQuickActions:
		if (quickActionsWindow_->IsVisible())
		{
			quickActionsWindow_->Hide();
		}
		else if (!quickActionsWindow_->IsEmpty())
		{
			CapturePreviousForegroundWindow();
			const auto monitor = CaptureTargetMonitor();
			quickActionsWindow_->Show(monitor, previousForegroundHwnd_);
		}
		return S_OK;
	case RequestKind::HidePopups:
		inputWindow_->Hide();
		quickActionsWindow_->Hide();
		return S_OK;
	default:
		return E_INVALIDARG;
	}
}

HRESULT NativeShellHost::Shutdown()
{
	std::lock_guard lock(lifecycleMutex_);
	if (threadHandle_ == nullptr)
	{
		stopping_ = false;
		return S_OK;
	}

	if (startedEvent_ != nullptr)
	{
		const auto startupWait = WaitForSingleObject(startedEvent_, ShutdownTimeoutMs);
		if (startupWait == WAIT_TIMEOUT)
		{
			return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
		}
		if (startupWait != WAIT_OBJECT_0)
		{
			return LastErrorAsHresult();
		}
		CloseHandle(startedEvent_);
		startedEvent_ = nullptr;
	}
	if (GetCurrentThreadId() == threadId_)
	{
		if (stopping_)
		{
			return S_OK;
		}
		stopping_ = true;
		if (PostThreadMessageW(threadId_, HostShutdownMessage, 0, 0))
		{
			return S_OK;
		}
		stopping_ = false;
		return LastErrorAsHresult();
	}

	const auto initialThreadState = WaitForSingleObject(threadHandle_, 0);
	if (initialThreadState == WAIT_FAILED)
	{
		return LastErrorAsHresult();
	}
	if (initialThreadState == WAIT_TIMEOUT)
	{
		if (!stopping_)
		{
			stopping_ = true;
			if (!PostThreadMessageW(threadId_, HostShutdownMessage, 0, 0))
			{
				stopping_ = false;
				return LastErrorAsHresult();
			}
		}
		const auto shutdownWait = WaitForSingleObject(threadHandle_, ShutdownTimeoutMs);
		if (shutdownWait == WAIT_TIMEOUT)
		{
			return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
		}
		if (shutdownWait != WAIT_OBJECT_0)
		{
			return LastErrorAsHresult();
		}
	}

	CloseHandle(threadHandle_);
	threadHandle_ = nullptr;
	threadId_ = 0;
	startResult_ = E_PENDING;
	stopping_ = false;
	return S_OK;
}

DWORD WINAPI NativeShellHost::ThreadEntry(LPVOID parameter)
{
	auto* host = static_cast<NativeShellHost*>(parameter);
	HRESULT result = E_FAIL;
	try
	{
		result = host->Run();
	}
	catch (...)
	{
		if (host->startResult_ == E_PENDING)
		{
			host->startResult_ = E_FAIL;
			if (host->startedEvent_ != nullptr)
			{
				SetEvent(host->startedEvent_);
			}
		}
	}
	return static_cast<DWORD>(FAILED(result) ? result : 0);
}

HRESULT NativeShellHost::Run()
{
	const auto previousDpiContext = SetThreadDpiAwarenessContext(
		DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
	const auto comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	const bool shouldUninitialize = SUCCEEDED(comResult);
	if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
	{
		startResult_ = comResult;
		SetEvent(startedEvent_);
		return comResult;
	}

	auto result = EnsureFactories();
	if (SUCCEEDED(result))
	{
		try
		{
			inputWindow_ = std::make_unique<InputWindow>(
				d2dFactory_.Get(),
				dwriteFactory_.Get(),
				[this](const std::wstring& text) { OnInputSubmitted(text); });
			quickActionsWindow_ = std::make_unique<QuickActionsWindow>(
				d2dFactory_.Get(),
				dwriteFactory_.Get(),
				[this](uint64_t token) { OnQuickActionActivated(token); });
			result = CreateWindows();
		}
		catch (...)
		{
			result = E_OUTOFMEMORY;
		}
	}

	startResult_ = result;
	if (startedEvent_ != nullptr) SetEvent(startedEvent_);
	if (FAILED(result))
	{
		DestroyWindows();
		quickActionsWindow_.reset();
		inputWindow_.reset();
		dwriteFactory_.Reset();
		d2dFactory_.Reset();
		if (shouldUninitialize) CoUninitialize();
		if (previousDpiContext != nullptr) SetThreadDpiAwarenessContext(previousDpiContext);
		return result;
	}

	MSG message{};
	while (true)
	{
		const auto getMessageResult = GetMessageW(&message, nullptr, 0, 0);
		if (getMessageResult <= 0)
		{
			result = getMessageResult < 0 ? LastErrorAsHresult() : S_OK;
			break;
		}

		if (message.hwnd == nullptr && message.message == HostRequestMessage)
		{
			auto* request = reinterpret_cast<HostRequest*>(message.lParam);
			HRESULT requestResult = HRESULT_FROM_WIN32(ERROR_CANCELLED);
			if (request != nullptr && request->TryBeginExecution())
			{
				try
				{
					requestResult = ProcessRequest(*request);
				}
				catch (...)
				{
					requestResult = E_FAIL;
				}
			}
			CompleteRequest(request, requestResult);
			continue;
		}

		if (message.hwnd == nullptr && message.message == HostShutdownMessage)
		{
			DestroyWindows();
			PostQuitMessage(0);
			continue;
		}

		TranslateMessage(&message);
		DispatchMessageW(&message);
	}

	DestroyWindows();
	quickActionsWindow_.reset();
	inputWindow_.reset();
	dwriteFactory_.Reset();
	d2dFactory_.Reset();
	previousForegroundHwnd_ = nullptr;
	inputSubmittedCallback_ = nullptr;
	inputSubmittedContext_ = nullptr;
	quickActionActivatedCallback_ = nullptr;
	quickActionActivatedContext_ = nullptr;

	if (shouldUninitialize) CoUninitialize();
	if (previousDpiContext != nullptr) SetThreadDpiAwarenessContext(previousDpiContext);
	return result;
}

HRESULT NativeShellHost::EnsureFactories()
{
	if (!d2dFactory_)
	{
		auto result = D2D1CreateFactory(
			D2D1_FACTORY_TYPE_SINGLE_THREADED,
			d2dFactory_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!dwriteFactory_)
	{
		auto result = DWriteCreateFactory(
			DWRITE_FACTORY_TYPE_SHARED,
			__uuidof(IDWriteFactory),
			reinterpret_cast<IUnknown**>(dwriteFactory_.GetAddressOf()));
		if (FAILED(result)) return result;
	}
	return S_OK;
}

HRESULT NativeShellHost::CreateWindows()
{
	auto result = CreateWindowForKind(WindowKind::Input);
	if (FAILED(result)) return result;

	result = CreateWindowForKind(WindowKind::QuickActions);
	if (FAILED(result))
	{
		if (inputWindow_ != nullptr && inputWindow_->WindowHandle() != nullptr)
		{
			DestroyWindow(inputWindow_->WindowHandle());
		}
		return result;
	}

	inputWindow_->SetPeerWindow(quickActionsWindow_->WindowHandle());
	quickActionsWindow_->SetPeerWindow(inputWindow_->WindowHandle());
	return S_OK;
}

HRESULT NativeShellHost::CreateWindowForKind(WindowKind kind)
{
	const auto* className = kind == WindowKind::Input
		? InputWindowClassName
		: QuickActionsWindowClassName;
	WNDCLASSEXW windowClass{};
	windowClass.cbSize = sizeof(windowClass);
	windowClass.lpfnWndProc = &NativeShellHost::WindowProc;
	windowClass.hInstance = GetModuleHandleW(nullptr);
	windowClass.lpszClassName = className;
	windowClass.hCursor = LoadCursorW(
		nullptr,
		kind == WindowKind::Input ? IDC_IBEAM : IDC_ARROW);
	if (RegisterClassExW(&windowClass) == 0
		&& GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
	{
		return LastErrorAsHresult();
	}

	const int width = kind == WindowKind::Input
		? inputWindow_->PixelWidth()
		: quickActionsWindow_->PixelWidth();
	const int height = kind == WindowKind::Input
		? inputWindow_->PixelHeight()
		: quickActionsWindow_->PixelHeight();
	auto* context = kind == WindowKind::Input
		? &inputWindowContext_
		: &quickActionsWindowContext_;
	const auto window = CreateWindowExW(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
		className,
		kind == WindowKind::Input ? L"LuvLetter Input" : L"LuvLetter Quick Actions",
		WS_POPUP,
		CW_USEDEFAULT,
		CW_USEDEFAULT,
		width,
		height,
		nullptr,
		nullptr,
		GetModuleHandleW(nullptr),
		context);
	if (window == nullptr) return LastErrorAsHresult();

	const auto attachResult = kind == WindowKind::Input
		? inputWindow_->Attach(window)
		: quickActionsWindow_->Attach(window);
	if (FAILED(attachResult))
	{
		DestroyWindow(window);
		return attachResult;
	}
	return S_OK;
}

void NativeShellHost::DestroyWindows() noexcept
{
	if (quickActionsWindow_ != nullptr && quickActionsWindow_->WindowHandle() != nullptr)
	{
		DestroyWindow(quickActionsWindow_->WindowHandle());
	}
	if (inputWindow_ != nullptr && inputWindow_->WindowHandle() != nullptr)
	{
		DestroyWindow(inputWindow_->WindowHandle());
	}
}

HMONITOR NativeShellHost::CaptureTargetMonitor() const
{
	const auto foreground = GetForegroundWindow();
	if (foreground != nullptr)
	{
		return MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
	}
	POINT cursor{};
	if (GetCursorPos(&cursor))
	{
		return MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
	}
	return MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
}

void NativeShellHost::CapturePreviousForegroundWindow() noexcept
{
	const auto foreground = GetForegroundWindow();
	const auto input = inputWindow_ == nullptr ? nullptr : inputWindow_->WindowHandle();
	const auto quickActions = quickActionsWindow_ == nullptr
		? nullptr
		: quickActionsWindow_->WindowHandle();
	if (foreground != nullptr && foreground != input && foreground != quickActions)
	{
		previousForegroundHwnd_ = foreground;
	}
}

void NativeShellHost::OnInputSubmitted(const std::wstring& text) noexcept
{
	const auto callback = inputSubmittedCallback_;
	const auto context = inputSubmittedContext_;
	if (callback == nullptr) return;
	const auto length = static_cast<int32_t>((std::min)(
		text.size(),
		static_cast<size_t>((std::numeric_limits<int32_t>::max)())));
	__try
	{
		callback(text.c_str(), length, context);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
	}
}

void NativeShellHost::OnQuickActionActivated(uint64_t token) noexcept
{
	const auto callback = quickActionActivatedCallback_;
	const auto context = quickActionActivatedContext_;
	if (callback == nullptr) return;
	__try
	{
		callback(token, context);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
	}
}

LRESULT NativeShellHost::DispatchWindowMessage(
	WindowKind kind,
	HWND window,
	UINT message,
	WPARAM wParam,
	LPARAM lParam)
{
	return kind == WindowKind::Input
		? inputWindow_->HandleMessage(window, message, wParam, lParam)
		: quickActionsWindow_->HandleMessage(window, message, wParam, lParam);
}

LRESULT NativeShellHost::DispatchWindowProc(
	HWND window,
	UINT message,
	WPARAM wParam,
	LPARAM lParam) noexcept
{
	try
	{
		if (message == WM_NCCREATE)
		{
			const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
			auto* context = static_cast<WindowContext*>(createStruct->lpCreateParams);
			if (context == nullptr || context->host == nullptr) return FALSE;
			SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(context));
			return TRUE;
		}
		auto* context = reinterpret_cast<WindowContext*>(
			GetWindowLongPtrW(window, GWLP_USERDATA));
		if (context == nullptr || context->host == nullptr)
		{
			return DefWindowProcW(window, message, wParam, lParam);
		}
		return context->host->DispatchWindowMessage(
			context->kind,
			window,
			message,
			wParam,
			lParam);
	}
	catch (...)
	{
		return DefWindowProcW(window, message, wParam, lParam);
	}
}

LRESULT CALLBACK NativeShellHost::WindowProc(
	HWND window,
	UINT message,
	WPARAM wParam,
	LPARAM lParam) noexcept
{
	__try
	{
		return DispatchWindowProc(window, message, wParam, lParam);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return DefWindowProcW(window, message, wParam, lParam);
	}
}


NativeShellHost::~NativeShellHost() = default;
