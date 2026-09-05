#include "host/NativeShellHost.h"

#include "rendering/LayeredWindowSurface.h"
#include "windows/MessageQueueWindow.h"
#include "windows/QuickActionsWindow.h"
#include "windows/InputWindow.h"
#include "windows/InputCandidatesWindow.h"

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
	constexpr wchar_t InputCandidatesWindowClassName[] = L"LuvLetter.Native.InputCandidates";
	constexpr wchar_t QuickActionsWindowClassName[] = L"LuvLetter.Native.QuickActionsWindow";
	constexpr wchar_t MessageQueueWindowClassName[] = L"LuvLetter.Native.MessageQueueWindow";
	constexpr UINT HostRequestMessage = WM_APP + 40;
	constexpr UINT HostShutdownMessage = WM_APP + 41;
	constexpr DWORD StartupTimeoutMs = 10000;
	constexpr DWORD RequestTimeoutMs = 5000;
	constexpr DWORD ShutdownTimeoutMs = 5000;
	constexpr int32_t MaxQuickActions = 4096;
	constexpr int32_t MaxMessageLength = 4096;

	enum class RequestKind
	{
		ApplyInputConfig,
		SetInputCallback,
		SetInputChangedCallback,
		SetInputCandidates,
		SetCandidateCallback,
		ShowInput,
		HideInput,
		ToggleInput,
		ApplyQuickActionsConfig,
		SetQuickActions,
		SetQuickActionCallback,
		ShowQuickActions,
		HideQuickActions,
		ToggleQuickActions,
		EnqueueMessage,
		BeginMessageActivity,
		UpdateMessageActivity,
		CompleteMessageActivity,
		ToggleMessageQueue,
		HideMessageQueue,
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
	std::vector<InputCandidateItem> inputCandidates;
	std::wstring message;
	uint64_t messageActivityToken = 0;
	bool retainFinalMessage = false;
	uint64_t revision = 0;
	LuvLetterInputSubmittedCallback inputCallback = nullptr;
	LuvLetterInputChangedCallback inputChangedCallback = nullptr;
	LuvLetterCandidateActivatedCallback candidateCallback = nullptr;
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
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowInput, true);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, true);
}

HRESULT NativeShellHost::Hide()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::Toggle()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleInput, true);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, true);
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
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowQuickActions, true);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, true);
}

HRESULT NativeShellHost::HideQuickActionsWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideQuickActions, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::ToggleQuickActionsWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleQuickActions, true);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, true);
}

HRESULT NativeShellHost::EnqueueMessage(const wchar_t* text, int32_t length)
{
	if (text == nullptr || length <= 0 || length > MaxMessageLength)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::EnqueueMessage, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	try
	{
		request->message.assign(text, static_cast<size_t>(length));
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::BeginMessageActivity(
	uint64_t token,
	const wchar_t* text,
	int32_t length)
{
	if (token == 0 || text == nullptr || length <= 0 || length > MaxMessageLength)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::BeginMessageActivity, true);
	if (request == nullptr) return E_OUTOFMEMORY;
	request->messageActivityToken = token;
	try
	{
		request->message.assign(text, static_cast<size_t>(length));
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::UpdateMessageActivity(
	uint64_t token,
	const wchar_t* text,
	int32_t length)
{
	if (token == 0 || text == nullptr || length <= 0 || length > MaxMessageLength)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::UpdateMessageActivity, true);
	if (request == nullptr) return E_OUTOFMEMORY;
	request->messageActivityToken = token;
	try
	{
		request->message.assign(text, static_cast<size_t>(length));
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::CompleteMessageActivity(
	uint64_t token,
	const wchar_t* finalText,
	int32_t length)
{
	if (token == 0
		|| length < 0
		|| length > MaxMessageLength
		|| (length > 0 && finalText == nullptr))
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::CompleteMessageActivity, true);
	if (request == nullptr) return E_OUTOFMEMORY;
	request->messageActivityToken = token;
	request->retainFinalMessage = length > 0;
	try
	{
		if (length > 0)
		{
			request->message.assign(finalText, static_cast<size_t>(length));
		}
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetInputChangedCallback(
	LuvLetterInputChangedCallback callback,
	void* context)
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::SetInputChangedCallback, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->inputChangedCallback = callback;
	request->callbackContext = context;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetCandidateActivatedCallback(
	LuvLetterCandidateActivatedCallback callback,
	void* context)
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::SetCandidateCallback, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->candidateCallback = callback;
	request->callbackContext = context;
	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::SetInputCandidates(
	const LuvLetterInputCandidate* items,
	int32_t count,
	uint64_t revision)
{
	if (count < 0 || count > LUVLETTER_NATIVE_MAX_INPUT_CANDIDATES
		|| (count > 0 && items == nullptr))
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::SetInputCandidates, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->revision = revision;

	try
	{
		request->inputCandidates.reserve(static_cast<size_t>(count));
		for (int32_t index = 0; index < count; ++index)
		{
			const auto& source = items[index];
			if (source.token == 0
				|| source.primaryText == nullptr
				|| source.kind < LuvLetterCandidateKindFile
				|| source.kind > LuvLetterCandidateKindGlobalSearch
				|| source.iconKind < LuvLetterCandidateIconKindNone
				|| source.iconKind > LuvLetterCandidateIconKindSearch)
			{
				request->Release();
				return E_INVALIDARG;
			}

			const auto primaryLength = wcsnlen_s(
				source.primaryText,
				static_cast<size_t>(LUVLETTER_NATIVE_MAX_CANDIDATE_PRIMARY_LENGTH) + 1);
			if (primaryLength > LUVLETTER_NATIVE_MAX_CANDIDATE_PRIMARY_LENGTH)
			{
				request->Release();
				return E_INVALIDARG;
			}

			InputCandidateItem item{};
			item.token = source.token;
			item.kind = static_cast<LuvLetterCandidateKind>(source.kind);
			item.iconKind = static_cast<LuvLetterCandidateIconKind>(source.iconKind);
			item.primaryText.assign(source.primaryText, primaryLength);
			if (source.secondaryText != nullptr)
			{
				const auto secondaryLength = wcsnlen_s(
					source.secondaryText,
					static_cast<size_t>(LUVLETTER_NATIVE_MAX_CANDIDATE_SECONDARY_LENGTH) + 1);
				if (secondaryLength > LUVLETTER_NATIVE_MAX_CANDIDATE_SECONDARY_LENGTH)
				{
					request->Release();
					return E_INVALIDARG;
				}
				item.secondaryText.assign(source.secondaryText, secondaryLength);
			}
			request->inputCandidates.push_back(std::move(item));
		}
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}

	return DispatchRequest(request, true);
}

HRESULT NativeShellHost::ToggleMessageQueue()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleMessageQueue, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT NativeShellHost::HideMessageQueue()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideMessageQueue, false);
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
	if (inputWindow_ == nullptr || inputCandidatesWindow_ == nullptr
		|| quickActionsWindow_ == nullptr
		|| messageQueueWindow_ == nullptr)
	{
		return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
	}

	switch (request.kind)
	{
	case RequestKind::ApplyInputConfig:
		inputWindow_->ApplyConfiguration(request.inputConfig);
		inputCandidatesWindow_->ApplyConfiguration(request.inputConfig);
		return S_OK;
	case RequestKind::SetInputCallback:
		inputSubmittedCallback_ = request.inputCallback;
		inputSubmittedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::SetInputChangedCallback:
		inputChangedCallback_ = request.inputChangedCallback;
		inputChangedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::SetCandidateCallback:
		candidateActivatedCallback_ = request.candidateCallback;
		candidateActivatedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::SetInputCandidates:
		return inputCandidatesWindow_->SetItems(
			std::move(request.inputCandidates),
			request.revision,
			inputWindow_->CurrentRevision(),
			inputWindow_->IsVisible())
			? S_OK
			: S_FALSE;
	case RequestKind::ShowInput:
	{
		CapturePreviousForegroundWindow();
		quickActionsWindow_->Hide();
		const auto monitor = CaptureTargetMonitor();
		inputWindow_->Show(monitor, previousForegroundHwnd_);
		const auto activated = TryActivateInteractiveWindow(
			inputWindow_->WindowHandle(),
			previousForegroundHwnd_);
		inputWindow_->RefreshFocusVisuals();
		return activated
			? S_OK
			: HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
	}
	case RequestKind::HideInput:
		inputWindow_->Hide();
		inputCandidatesWindow_->Hide();
		return S_OK;
	case RequestKind::ToggleInput:
		if (inputWindow_->IsVisible() && inputWindow_->HasKeyboardFocus())
		{
			inputWindow_->Hide();
			inputCandidatesWindow_->Hide();
		}
		else if (!inputWindow_->IsVisible())
		{
			CapturePreviousForegroundWindow();
			quickActionsWindow_->Hide();
			const auto monitor = CaptureTargetMonitor();
			inputWindow_->Show(monitor, previousForegroundHwnd_);
			const auto activated = TryActivateInteractiveWindow(
				inputWindow_->WindowHandle(),
				previousForegroundHwnd_);
			inputWindow_->RefreshFocusVisuals();
			return activated
				? S_OK
				: HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
		}
		else
		{
			CapturePreviousForegroundWindow();
			quickActionsWindow_->Hide();
			inputWindow_->SetPreviousForegroundWindow(previousForegroundHwnd_);
			const auto activated = TryActivateInteractiveWindow(
				inputWindow_->WindowHandle(),
				previousForegroundHwnd_);
			inputWindow_->RefreshFocusVisuals();
			return activated
				? S_OK
				: HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
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
		inputWindow_->Hide();
		inputCandidatesWindow_->Hide();
		{
			const auto monitor = CaptureTargetMonitor();
			quickActionsWindow_->Show(monitor, previousForegroundHwnd_);
		}
		return TryActivateInteractiveWindow(
			quickActionsWindow_->WindowHandle(),
			previousForegroundHwnd_)
			? S_OK
			: HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
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
			inputWindow_->Hide();
			inputCandidatesWindow_->Hide();
			const auto monitor = CaptureTargetMonitor();
			quickActionsWindow_->Show(monitor, previousForegroundHwnd_);
			return TryActivateInteractiveWindow(
				quickActionsWindow_->WindowHandle(),
				previousForegroundHwnd_)
				? S_OK
				: HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
		}
		return S_OK;
	case RequestKind::EnqueueMessage:
		return messageQueueWindow_->Enqueue(
			std::move(request.message),
			CaptureTargetMonitor());
	case RequestKind::BeginMessageActivity:
		return messageQueueWindow_->BeginActivity(
			request.messageActivityToken,
			std::move(request.message),
			CaptureTargetMonitor());
	case RequestKind::UpdateMessageActivity:
		return messageQueueWindow_->UpdateActivity(
			request.messageActivityToken,
			std::move(request.message));
	case RequestKind::CompleteMessageActivity:
		return messageQueueWindow_->CompleteActivity(
			request.messageActivityToken,
			std::move(request.message),
			request.retainFinalMessage,
			CaptureTargetMonitor());
	case RequestKind::ToggleMessageQueue:
		messageQueueWindow_->Toggle(CaptureTargetMonitor());
		return S_OK;
	case RequestKind::HideMessageQueue:
		messageQueueWindow_->Hide();
		return S_OK;
	case RequestKind::HidePopups:
		inputWindow_->Hide();
		inputCandidatesWindow_->Hide();
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
				[this](const std::wstring& text, int32_t inputMode)
				{
					OnInputSubmitted(text, inputMode);
				},
				[this](const std::wstring& text, int32_t inputMode, uint64_t revision)
				{
					OnInputChanged(text, inputMode, revision);
				},
				[this](int direction)
				{
					return inputCandidatesWindow_ != nullptr
						&& inputCandidatesWindow_->MoveSelection(direction);
				},
				[this](int32_t action)
				{
					return inputCandidatesWindow_ != nullptr
						&& inputCandidatesWindow_->ActivateSelected(
							static_cast<LuvLetterCandidateAction>(action));
				},
				[this]()
				{
					if (inputCandidatesWindow_ != nullptr)
					{
						inputCandidatesWindow_->Hide();
					}
				});
			inputCandidatesWindow_ = std::make_unique<InputCandidatesWindow>(
				d2dFactory_.Get(),
				dwriteFactory_.Get(),
				[this](uint64_t token, int32_t action)
				{
					OnCandidateActivated(token, action);
				});
			quickActionsWindow_ = std::make_unique<QuickActionsWindow>(
				d2dFactory_.Get(),
				dwriteFactory_.Get(),
				[this](uint64_t token) { OnQuickActionActivated(token); });
			messageQueueWindow_ = std::make_unique<MessageQueueWindow>(
				d2dFactory_.Get(),
				dwriteFactory_.Get());
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
		messageQueueWindow_.reset();
		quickActionsWindow_.reset();
		inputCandidatesWindow_.reset();
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
	messageQueueWindow_.reset();
	quickActionsWindow_.reset();
	inputCandidatesWindow_.reset();
	inputWindow_.reset();
	dwriteFactory_.Reset();
	d2dFactory_.Reset();
	previousForegroundHwnd_ = nullptr;
	inputSubmittedCallback_ = nullptr;
	inputSubmittedContext_ = nullptr;
	inputChangedCallback_ = nullptr;
	inputChangedContext_ = nullptr;
	candidateActivatedCallback_ = nullptr;
	candidateActivatedContext_ = nullptr;
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

	result = CreateWindowForKind(WindowKind::InputCandidates);
	if (FAILED(result))
	{
		DestroyWindows();
		return result;
	}

	result = CreateWindowForKind(WindowKind::QuickActions);
	if (FAILED(result))
	{
		DestroyWindows();
		return result;
	}

	result = CreateWindowForKind(WindowKind::MessageQueue);
	if (FAILED(result))
	{
		DestroyWindows();
		return result;
	}

	inputWindow_->SetPeerWindow(quickActionsWindow_->WindowHandle());
	quickActionsWindow_->SetPeerWindow(inputWindow_->WindowHandle());
	return S_OK;
}

HRESULT NativeShellHost::CreateWindowForKind(WindowKind kind)
{
	const wchar_t* className = nullptr;
	const wchar_t* title = nullptr;
	int width = 0;
	int height = 0;
	WindowContext* context = nullptr;
	DWORD extendedStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
	HRESULT attachResult = E_INVALIDARG;

	switch (kind)
	{
	case WindowKind::Input:
		className = InputWindowClassName;
		title = L"LuvLetter Input";
		width = inputWindow_->PixelWidth();
		height = inputWindow_->PixelHeight();
		context = &inputWindowContext_;
		break;
	case WindowKind::InputCandidates:
		className = InputCandidatesWindowClassName;
		title = L"LuvLetter Input Candidates";
		width = inputCandidatesWindow_->PixelWidth();
		height = inputCandidatesWindow_->PixelHeight();
		context = &inputCandidatesWindowContext_;
		extendedStyle |= WS_EX_NOACTIVATE;
		break;
	case WindowKind::QuickActions:
		className = QuickActionsWindowClassName;
		title = L"LuvLetter Quick Actions";
		width = quickActionsWindow_->PixelWidth();
		height = quickActionsWindow_->PixelHeight();
		context = &quickActionsWindowContext_;
		break;
	case WindowKind::MessageQueue:
		className = MessageQueueWindowClassName;
		title = L"LuvLetter Message Queue";
		width = messageQueueWindow_->PixelWidth();
		height = messageQueueWindow_->PixelHeight();
		context = &messageQueueWindowContext_;
		extendedStyle |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
		break;
	default:
		return E_INVALIDARG;
	}

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

	const auto window = CreateWindowExW(
		extendedStyle,
		className,
		title,
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

	switch (kind)
	{
	case WindowKind::Input:
		attachResult = inputWindow_->Attach(window);
		break;
	case WindowKind::InputCandidates:
		attachResult = inputCandidatesWindow_->Attach(
			window,
			inputWindow_->WindowHandle());
		break;
	case WindowKind::QuickActions:
		attachResult = quickActionsWindow_->Attach(window);
		break;
	case WindowKind::MessageQueue:
		attachResult = messageQueueWindow_->Attach(window);
		break;
	}
	if (FAILED(attachResult))
	{
		DestroyWindow(window);
		return attachResult;
	}
	return S_OK;
}

void NativeShellHost::DestroyWindows() noexcept
{
	if (messageQueueWindow_ != nullptr && messageQueueWindow_->WindowHandle() != nullptr)
	{
		DestroyWindow(messageQueueWindow_->WindowHandle());
	}
	if (quickActionsWindow_ != nullptr && quickActionsWindow_->WindowHandle() != nullptr)
	{
		DestroyWindow(quickActionsWindow_->WindowHandle());
	}
	if (inputCandidatesWindow_ != nullptr
		&& inputCandidatesWindow_->WindowHandle() != nullptr)
	{
		DestroyWindow(inputCandidatesWindow_->WindowHandle());
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
	const auto inputCandidates = inputCandidatesWindow_ == nullptr
		? nullptr
		: inputCandidatesWindow_->WindowHandle();
	const auto quickActions = quickActionsWindow_ == nullptr
		? nullptr
		: quickActionsWindow_->WindowHandle();
	const auto messageQueue = messageQueueWindow_ == nullptr
		? nullptr
		: messageQueueWindow_->WindowHandle();
	if (foreground != nullptr && foreground != input && foreground != inputCandidates
		&& foreground != quickActions
		&& foreground != messageQueue)
	{
		previousForegroundHwnd_ = foreground;
	}
}

bool NativeShellHost::TryActivateInteractiveWindow(
	HWND target,
	HWND previousForeground) noexcept
{
	if (target == nullptr || !IsWindow(target) || !IsWindowEnabled(target))
	{
		return false;
	}

	const auto activateAndVerify = [target]() noexcept
	{
		BringWindowToTop(target);
		SetForegroundWindow(target);
		SetActiveWindow(target);
		SetFocus(target);
		return GetForegroundWindow() == target && GetFocus() == target;
	};

	if (activateAndVerify())
	{
		return true;
	}

	auto inputSource = previousForeground;
	if (inputSource == nullptr || inputSource == target || !IsWindow(inputSource))
	{
		inputSource = GetForegroundWindow();
	}
	if (inputSource == nullptr || inputSource == target || !IsWindow(inputSource))
	{
		return false;
	}

	const auto currentThreadId = GetCurrentThreadId();
	const auto inputThreadId = GetWindowThreadProcessId(inputSource, nullptr);
	if (inputThreadId == 0 || inputThreadId == currentThreadId)
	{
		return false;
	}
	if (!AttachThreadInput(currentThreadId, inputThreadId, TRUE))
	{
		return false;
	}

	const auto activated = activateAndVerify();
	AttachThreadInput(currentThreadId, inputThreadId, FALSE);
	return activated
		&& GetForegroundWindow() == target
		&& GetFocus() == target;
}

void NativeShellHost::OnInputSubmitted(const std::wstring& text, int32_t inputMode) noexcept
{
	const auto callback = inputSubmittedCallback_;
	const auto context = inputSubmittedContext_;
	if (callback == nullptr) return;
	const auto length = static_cast<int32_t>((std::min)(
		text.size(),
		static_cast<size_t>((std::numeric_limits<int32_t>::max)())));
	__try
	{
		callback(text.c_str(), length, inputMode, context);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
	}
}

void NativeShellHost::OnInputChanged(
	const std::wstring& text,
	int32_t inputMode,
	uint64_t revision) noexcept
{
	if (inputCandidatesWindow_ != nullptr)
	{
		inputCandidatesWindow_->Clear();
	}
	const auto callback = inputChangedCallback_;
	const auto context = inputChangedContext_;
	if (callback == nullptr) return;
	const auto length = static_cast<int32_t>((std::min)(
		text.size(),
		static_cast<size_t>((std::numeric_limits<int32_t>::max)())));
	__try
	{
		callback(text.c_str(), length, inputMode, revision, context);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
	}
}

void NativeShellHost::OnCandidateActivated(uint64_t token, int32_t action) noexcept
{
	const auto callback = candidateActivatedCallback_;
	const auto context = candidateActivatedContext_;
	if (callback == nullptr) return;
	__try
	{
		callback(token, action, context);
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
	switch (kind)
	{
	case WindowKind::Input:
	{
		const auto result = inputWindow_->HandleMessage(window, message, wParam, lParam);
		if (inputCandidatesWindow_ != nullptr
			&& (message == WM_WINDOWPOSCHANGED
				|| message == WM_SIZE
				|| message == WM_DPICHANGED
				|| message == WM_DISPLAYCHANGE
				|| message == WM_SETTINGCHANGE))
		{
			inputCandidatesWindow_->SynchronizeToInputWindow();
		}
		return result;
	}
	case WindowKind::InputCandidates:
		return inputCandidatesWindow_->HandleMessage(window, message, wParam, lParam);
	case WindowKind::QuickActions:
		return quickActionsWindow_->HandleMessage(window, message, wParam, lParam);
	case WindowKind::MessageQueue:
		return messageQueueWindow_->HandleMessage(window, message, wParam, lParam);
	default:
		return DefWindowProcW(window, message, wParam, lParam);
	}
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
