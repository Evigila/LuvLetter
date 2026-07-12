#include "input/InputBoxHost.h"

#include <imm.h>
#include <windowsx.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <limits>
#include <new>
#include <utility>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "imm32.lib")

namespace
{
	constexpr wchar_t InputWindowClassName[] = L"LuvLetter.Native.InputBox";
	constexpr wchar_t FeatureWindowClassName[] = L"LuvLetter.Native.FeatureWindow";
	constexpr UINT HostRequestMessage = WM_APP + 40;
	constexpr UINT HostShutdownMessage = WM_APP + 41;
	constexpr UINT_PTR CaretTimerId = 1;
	constexpr UINT CaretBlinkMs = 530;
	constexpr DWORD StartupTimeoutMs = 10000;
	constexpr DWORD RequestTimeoutMs = 5000;
	constexpr DWORD ShutdownTimeoutMs = 5000;
	constexpr size_t MaxHistoryItems = 100;
	constexpr size_t MaxInputCharacters = 32768;
	constexpr int32_t MaxFeatureItems = 4096;
	constexpr float MaxTextLayoutWidth = 16777216.0f;
	constexpr wchar_t PlaceholderText[] = L"Enter command here";
	constexpr UINT DefaultDpi = 96;

	UINT NormalizeDpi(UINT dpi) noexcept
	{
		return dpi >= 48 && dpi <= 960 ? dpi : DefaultDpi;
	}

	int DipToPixels(float value, UINT dpi) noexcept
	{
		const auto scaled = std::round(
			static_cast<double>(value) * static_cast<double>(NormalizeDpi(dpi))
			/ static_cast<double>(DefaultDpi));
		return static_cast<int>((std::clamp)(
			scaled,
			static_cast<double>((std::numeric_limits<int>::min)()),
			static_cast<double>((std::numeric_limits<int>::max)())));
	}

	float PixelsToDip(int value, UINT dpi) noexcept
	{
		return static_cast<float>(value) * static_cast<float>(DefaultDpi)
			/ static_cast<float>(NormalizeDpi(dpi));
	}

	D2D1_ROUNDED_RECT CreateInsetRoundedRect(
		float left,
		float top,
		float right,
		float bottom,
		float cornerRadius,
		float borderThickness) noexcept
	{
		const auto width = (std::max)(0.0f, right - left);
		const auto height = (std::max)(0.0f, bottom - top);
		// Leave half a DIP beyond the stroke so Direct2D's coverage pixels remain
		// inside the layered bitmap instead of being clipped at the surface edge.
		const auto requestedInset = 0.5f + (std::max)(0.0f, borderThickness) / 2.0f;
		const auto maximumInset = (std::max)(0.0f, (std::min)(width, height) / 2.0f - 0.01f);
		const auto inset = (std::min)(requestedInset, maximumInset);
		const auto rect = D2D1::RectF(left + inset, top + inset, right - inset, bottom - inset);
		const auto radius = (std::clamp)(
			cornerRadius,
			0.0f,
			(std::max)(0.0f, (std::min)(rect.right - rect.left, rect.bottom - rect.top) / 2.0f));
		return D2D1::RoundedRect(rect, radius, radius);
	}

	UINT QueryWindowDpi(HWND window) noexcept
	{
		using GetDpiForWindowFunction = UINT(WINAPI*)(HWND);
		static const auto getDpiForWindow = reinterpret_cast<GetDpiForWindowFunction>(
			GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
		if (window != nullptr && getDpiForWindow != nullptr)
		{
			const auto dpi = getDpiForWindow(window);
			if (dpi != 0)
			{
				return NormalizeDpi(dpi);
			}
		}

		const auto screenDc = GetDC(nullptr);
		if (screenDc == nullptr)
		{
			return DefaultDpi;
		}
		const auto dpi = static_cast<UINT>(GetDeviceCaps(screenDc, LOGPIXELSX));
		ReleaseDC(nullptr, screenDc);
		return NormalizeDpi(dpi);
	}

	enum class RequestKind
	{
		ApplyInputConfig,
		SetInputCallback,
		ShowInput,
		HideInput,
		ToggleInput,
		ApplyFeatureConfig,
		SetFeatureItems,
		SetFeatureCallback,
		ShowFeature,
		HideFeature,
		ToggleFeature,
	};

	LuvLetterInputBoxConfig CreateDefaultConfig()
	{
		LuvLetterInputBoxConfig config{};
		config.structSize = sizeof(config);
		config.abiVersion = LUVLETTER_NATIVE_ABI_VERSION;
		config.width = 640;
		config.height = 44;
		config.cornerRadius = 10.0f;
		config.borderThickness = 1.0f;
		config.fontSize = 20.0f;
		config.horizontalPadding = 10.0f;
		config.verticalPadding = 6.0f;
		config.caretWidth = 2.25f;
		config.positionMode = 0;
		config.bottomMargin = 60;
		config.borderColor = 0x66FFFFFF;
		config.backgroundColor = 0x38F5F5F5;
		config.textColor = 0xFFFFFFFF;
		config.caretColor = 0xFFFFFFFF;
		config.submitVirtualKey = VK_RETURN;
		config.cancelVirtualKey = VK_ESCAPE;
		config.backspaceVirtualKey = VK_BACK;
		return config;
	}

	LuvLetterFeatureWindowConfig CreateDefaultFeatureConfig()
	{
		LuvLetterFeatureWindowConfig config{};
		config.structSize = sizeof(config);
		config.abiVersion = LUVLETTER_NATIVE_ABI_VERSION;
		config.itemsPerPage = 7;
		config.cellSize = 96.0f;
		config.gap = 12.0f;
		config.cornerRadius = 16.0f;
		config.borderThickness = 1.0f;
		config.fontSize = 16.0f;
		config.bottomMargin = 60;
		config.borderColor = 0x66FFFFFF;
		config.backgroundColor = 0x38F5F5F5;
		config.textColor = 0xFFFFFFFF;
		config.accentColor = 0xFFFFFFFF;
		config.previousVirtualKey = VK_OEM_MINUS;
		config.nextVirtualKey = VK_OEM_PLUS;
		config.cancelVirtualKey = VK_ESCAPE;
		config.firstItemVirtualKey = L'1';
		return config;
	}

	D2D1_COLOR_F ColorFromArgb(uint32_t argb)
	{
		return D2D1::ColorF(
			static_cast<float>((argb >> 16) & 0xFF) / 255.0f,
			static_cast<float>((argb >> 8) & 0xFF) / 255.0f,
			static_cast<float>(argb & 0xFF) / 255.0f,
			static_cast<float>((argb >> 24) & 0xFF) / 255.0f);
	}

	D2D1_COLOR_F PlaceholderColorFromArgb(uint32_t argb)
	{
		auto color = ColorFromArgb(argb);
		color.a *= 0.48f;
		return color;
	}

	bool IsKeyDown(int virtualKey)
	{
		return (GetKeyState(virtualKey) & 0x8000) != 0;
	}

	int GetCurrentHotkeyModifiers()
	{
		int modifiers = 0;
		if (IsKeyDown(VK_MENU))
		{
			modifiers |= 1;
		}
		if (IsKeyDown(VK_CONTROL))
		{
			modifiers |= 2;
		}
		if (IsKeyDown(VK_SHIFT))
		{
			modifiers |= 4;
		}
		if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
		{
			modifiers |= 8;
		}
		return modifiers;
	}

	bool MatchesHotkey(WPARAM wParam, int virtualKey, int modifiers)
	{
		return virtualKey > 0
			&& wParam == static_cast<WPARAM>(virtualKey)
			&& GetCurrentHotkeyModifiers() == modifiers;
	}

	float FiniteOr(float value, float fallback)
	{
		return std::isfinite(value) ? value : fallback;
	}

	bool IsHighSurrogate(wchar_t value)
	{
		return value >= 0xD800 && value <= 0xDBFF;
	}

	bool IsLowSurrogate(wchar_t value)
	{
		return value >= 0xDC00 && value <= 0xDFFF;
	}

	size_t PreviousUtf16Boundary(const std::wstring& value, size_t index)
	{
		if (index == 0) return 0;
		if (index >= 2 && IsLowSurrogate(value[index - 1]) && IsHighSurrogate(value[index - 2]))
		{
			return index - 2;
		}
		return index - 1;
	}

	size_t NextUtf16Boundary(const std::wstring& value, size_t index)
	{
		if (index >= value.size()) return value.size();
		if (index + 1 < value.size() && IsHighSurrogate(value[index]) && IsLowSurrogate(value[index + 1]))
		{
			return index + 2;
		}
		return index + 1;
	}

	HRESULT LastErrorAsHresult()
	{
		const auto error = GetLastError();
		return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
	}

	void InvokeFeatureCallback(
		LuvLetterFeatureActivatedCallback callback,
		uint64_t token,
		void* context) noexcept
	{
		if (callback == nullptr)
		{
			return;
		}

		__try
		{
			callback(token, context);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
		}
	}

	void InvokeInputCallback(
		LuvLetterInputSubmittedCallback callback,
		const wchar_t* text,
		int32_t length,
		void* context) noexcept
	{
		if (callback == nullptr)
		{
			return;
		}

		__try
		{
			callback(text, length, context);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
		}
	}
}

struct InputBoxHost::FeatureItem
{
	uint64_t token = 0;
	std::wstring label;
};

struct InputBoxHost::CachedSurface
{
	HDC dc = nullptr;
	HBITMAP bitmap = nullptr;
	HGDIOBJ originalBitmap = nullptr;
	int width = 0;
	int height = 0;

	~CachedSurface()
	{
		Reset();
	}

	HRESULT Ensure(int requestedWidth, int requestedHeight)
	{
		requestedWidth = (std::max)(1, requestedWidth);
		requestedHeight = (std::max)(1, requestedHeight);
		if (dc != nullptr && bitmap != nullptr && width == requestedWidth && height == requestedHeight)
		{
			return S_OK;
		}

		Reset();
		const auto screenDc = GetDC(nullptr);
		if (screenDc == nullptr)
		{
			return LastErrorAsHresult();
		}

		dc = CreateCompatibleDC(screenDc);
		BITMAPINFO bitmapInfo{};
		bitmapInfo.bmiHeader.biSize = sizeof(bitmapInfo.bmiHeader);
		bitmapInfo.bmiHeader.biWidth = requestedWidth;
		bitmapInfo.bmiHeader.biHeight = -requestedHeight;
		bitmapInfo.bmiHeader.biPlanes = 1;
		bitmapInfo.bmiHeader.biBitCount = 32;
		bitmapInfo.bmiHeader.biCompression = BI_RGB;
		void* bitmapBits = nullptr;
		bitmap = CreateDIBSection(
			screenDc,
			&bitmapInfo,
			DIB_RGB_COLORS,
			&bitmapBits,
			nullptr,
			0);
		ReleaseDC(nullptr, screenDc);

		if (dc == nullptr || bitmap == nullptr)
		{
			const auto hr = LastErrorAsHresult();
			Reset();
			return hr;
		}

		originalBitmap = SelectObject(dc, bitmap);
		if (originalBitmap == nullptr || originalBitmap == HGDI_ERROR)
		{
			const auto hr = LastErrorAsHresult();
			originalBitmap = nullptr;
			Reset();
			return hr;
		}

		width = requestedWidth;
		height = requestedHeight;
		return S_OK;
	}

	void Reset() noexcept
	{
		if (dc != nullptr && originalBitmap != nullptr)
		{
			SelectObject(dc, originalBitmap);
		}
		if (bitmap != nullptr)
		{
			DeleteObject(bitmap);
		}
		if (dc != nullptr)
		{
			DeleteDC(dc);
		}

		dc = nullptr;
		bitmap = nullptr;
		originalBitmap = nullptr;
		width = 0;
		height = 0;
	}
};

struct InputBoxHost::HostRequest
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
	LuvLetterFeatureWindowConfig featureConfig{};
	std::vector<FeatureItem> featureItems;
	LuvLetterInputSubmittedCallback inputCallback = nullptr;
	LuvLetterFeatureActivatedCallback featureCallback = nullptr;
	void* callbackContext = nullptr;
};

InputBoxHost& InputBoxHost::Instance()
{
	static InputBoxHost instance;
	return instance;
}

InputBoxHost::InputBoxHost() = default;
InputBoxHost::~InputBoxHost() = default;

HRESULT InputBoxHost::ApplyConfig(const LuvLetterInputBoxConfig& config)
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

HRESULT InputBoxHost::SetInputSubmittedCallback(
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

HRESULT InputBoxHost::Show()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::Hide()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::Toggle()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleInput, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::ApplyFeatureConfig(const LuvLetterFeatureWindowConfig& config)
{
	if (config.structSize != sizeof(LuvLetterFeatureWindowConfig)
		|| config.abiVersion != LUVLETTER_NATIVE_ABI_VERSION)
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::ApplyFeatureConfig, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->featureConfig = config;
	return DispatchRequest(request, true);
}

HRESULT InputBoxHost::SetFeatureItems(const LuvLetterFeatureItem* items, int32_t count)
{
	if (count < 0 || count > MaxFeatureItems || (count > 0 && items == nullptr))
	{
		return E_INVALIDARG;
	}

	auto* request = new (std::nothrow) HostRequest(RequestKind::SetFeatureItems, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}

	try
	{
		request->featureItems.reserve(static_cast<size_t>(count));
		for (int32_t index = 0; index < count; ++index)
		{
			FeatureItem item{};
			item.token = items[index].token;
			if (items[index].label != nullptr)
			{
				item.label.assign(items[index].label, wcsnlen_s(items[index].label, 256));
			}
			request->featureItems.push_back(std::move(item));
		}
	}
	catch (...)
	{
		request->Release();
		return E_OUTOFMEMORY;
	}

	return DispatchRequest(request, true);
}

HRESULT InputBoxHost::SetFeatureActivatedCallback(
	LuvLetterFeatureActivatedCallback callback,
	void* context)
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::SetFeatureCallback, true);
	if (request == nullptr)
	{
		return E_OUTOFMEMORY;
	}
	request->featureCallback = callback;
	request->callbackContext = context;
	return DispatchRequest(request, true);
}

HRESULT InputBoxHost::ShowFeatureWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ShowFeature, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::HideFeatureWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::HideFeature, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::ToggleFeatureWindow()
{
	auto* request = new (std::nothrow) HostRequest(RequestKind::ToggleFeature, false);
	return request == nullptr ? E_OUTOFMEMORY : DispatchRequest(request, false);
}

HRESULT InputBoxHost::EnsureThread()
{
	std::lock_guard lock(lifecycleMutex_);
	return EnsureThreadLocked();
}

HRESULT InputBoxHost::EnsureThreadLocked()
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
	threadHandle_ = CreateThread(nullptr, 0, &InputBoxHost::ThreadEntry, this, 0, &threadId_);
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

HRESULT InputBoxHost::DispatchRequest(HostRequest* request, bool waitForCompletion)
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

void InputBoxHost::CompleteRequest(HostRequest* request, HRESULT result) noexcept
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

HRESULT InputBoxHost::ProcessRequest(HostRequest& request)
{
	switch (request.kind)
	{
	case RequestKind::ApplyInputConfig:
		ApplyConfigOnUiThread(request.inputConfig);
		return S_OK;
	case RequestKind::SetInputCallback:
		inputSubmittedCallback_ = request.inputCallback;
		inputSubmittedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::ShowInput:
		ShowInputWindowAndFocus();
		return S_OK;
	case RequestKind::HideInput:
		HideInputWindow();
		return S_OK;
	case RequestKind::ToggleInput:
		inputVisible_ ? HideInputWindow() : ShowInputWindowAndFocus();
		return S_OK;
	case RequestKind::ApplyFeatureConfig:
		ApplyFeatureConfigOnUiThread(request.featureConfig);
		return S_OK;
	case RequestKind::SetFeatureItems:
		SetFeatureItemsOnUiThread(std::move(request.featureItems));
		return S_OK;
	case RequestKind::SetFeatureCallback:
		featureActivatedCallback_ = request.featureCallback;
		featureActivatedContext_ = request.callbackContext;
		return S_OK;
	case RequestKind::ShowFeature:
		ShowFeatureWindowAndFocus();
		return featureItems_.empty() ? S_FALSE : S_OK;
	case RequestKind::HideFeature:
		HideFeatureWindowOnUiThread();
		return S_OK;
	case RequestKind::ToggleFeature:
		featureVisible_ ? HideFeatureWindowOnUiThread() : ShowFeatureWindowAndFocus();
		return S_OK;
	default:
		return E_INVALIDARG;
	}
}

HRESULT InputBoxHost::Shutdown()
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

DWORD WINAPI InputBoxHost::ThreadEntry(LPVOID parameter)
{
	auto* host = static_cast<InputBoxHost*>(parameter);
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

HRESULT InputBoxHost::Run()
{
	const auto previousDpiContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
	const auto comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	const bool shouldUninitialize = SUCCEEDED(comResult);
	if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
	{
		startResult_ = comResult;
		SetEvent(startedEvent_);
		return comResult;
	}

	config_ = CreateDefaultConfig();
	featureConfig_ = CreateDefaultFeatureConfig();
	inputSurface_ = std::make_unique<CachedSurface>();
	featureSurface_ = std::make_unique<CachedSurface>();

	auto result = CreateWindows();
	startResult_ = result;
	if (startedEvent_ != nullptr)
	{
		SetEvent(startedEvent_);
	}
	if (FAILED(result))
	{
		DiscardAllResources();
		if (shouldUninitialize)
		{
			CoUninitialize();
		}
		if (previousDpiContext != nullptr)
		{
			SetThreadDpiAwarenessContext(previousDpiContext);
		}
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
			if (featureHwnd_ != nullptr)
			{
				DestroyWindow(featureHwnd_);
			}
			if (inputHwnd_ != nullptr)
			{
				DestroyWindow(inputHwnd_);
			}
			PostQuitMessage(0);
			continue;
		}

		TranslateMessage(&message);
		DispatchMessageW(&message);
	}

	DiscardAllResources();
	inputHwnd_ = nullptr;
	featureHwnd_ = nullptr;
	inputVisible_ = false;
	featureVisible_ = false;
	text_.clear();
	featureItems_.clear();
	inputSubmittedCallback_ = nullptr;
	featureActivatedCallback_ = nullptr;

	if (shouldUninitialize)
	{
		CoUninitialize();
	}
	if (previousDpiContext != nullptr)
	{
		SetThreadDpiAwarenessContext(previousDpiContext);
	}
	return result;
}

HRESULT InputBoxHost::CreateWindows()
{
	auto result = CreateWindowForKind(WindowKind::Input);
	if (FAILED(result))
	{
		return result;
	}
	result = CreateWindowForKind(WindowKind::Feature);
	if (FAILED(result))
	{
		DestroyWindow(inputHwnd_);
		inputHwnd_ = nullptr;
		return result;
	}
	return S_OK;
}

HRESULT InputBoxHost::CreateWindowForKind(WindowKind kind)
{
	const auto* className = kind == WindowKind::Input ? InputWindowClassName : FeatureWindowClassName;
	WNDCLASSEXW windowClass{};
	windowClass.cbSize = sizeof(windowClass);
	windowClass.lpfnWndProc = &InputBoxHost::WindowProc;
	windowClass.hInstance = GetModuleHandleW(nullptr);
	windowClass.lpszClassName = className;
	windowClass.hCursor = LoadCursorW(nullptr, kind == WindowKind::Input ? IDC_IBEAM : IDC_ARROW);
	if (RegisterClassExW(&windowClass) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
	{
		return LastErrorAsHresult();
	}

	const int width = kind == WindowKind::Input
		? GetInputWindowPixelWidth()
		: GetFeatureWindowPixelWidth();
	const int height = kind == WindowKind::Input
		? GetInputWindowPixelHeight()
		: GetFeatureWindowPixelHeight();
	auto* context = kind == WindowKind::Input ? &inputWindowContext_ : &featureWindowContext_;
	const auto hwnd = CreateWindowExW(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
		className,
		kind == WindowKind::Input ? L"LuvLetter Input" : L"LuvLetter Features",
		WS_POPUP,
		CW_USEDEFAULT,
		CW_USEDEFAULT,
		width,
		height,
		nullptr,
		nullptr,
		GetModuleHandleW(nullptr),
		context);
	if (hwnd == nullptr)
	{
		return LastErrorAsHresult();
	}

	if (kind == WindowKind::Input)
	{
		inputHwnd_ = hwnd;
		inputDpi_ = QueryWindowDpi(hwnd);
		SetWindowPos(
			hwnd, nullptr, 0, 0,
			GetInputWindowPixelWidth(), GetInputWindowPixelHeight(),
			SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		UpdateInputWindowShape();
	}
	else
	{
		featureHwnd_ = hwnd;
		featureDpi_ = QueryWindowDpi(hwnd);
		SetWindowPos(
			hwnd, nullptr, 0, 0,
			GetFeatureWindowPixelWidth(), GetFeatureWindowPixelHeight(),
			SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		UpdateFeatureWindowShape();
	}
	return S_OK;
}

HRESULT InputBoxHost::EnsureFactories()
{
	if (!d2dFactory_)
	{
		auto result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf());
		if (FAILED(result))
		{
			return result;
		}
	}
	if (!dwriteFactory_)
	{
		auto result = DWriteCreateFactory(
			DWRITE_FACTORY_TYPE_SHARED,
			__uuidof(IDWriteFactory),
			reinterpret_cast<IUnknown**>(dwriteFactory_.GetAddressOf()));
		if (FAILED(result))
		{
			return result;
		}
	}
	return S_OK;
}

HRESULT InputBoxHost::EnsureInputResources()
{
	auto result = EnsureFactories();
	if (FAILED(result))
	{
		return result;
	}
	if (inputSurface_ == nullptr)
	{
		inputSurface_ = std::make_unique<CachedSurface>();
	}
	result = inputSurface_->Ensure(GetInputWindowPixelWidth(), GetInputWindowPixelHeight());
	if (FAILED(result))
	{
		return result;
	}
	if (!inputRenderTarget_)
	{
		const auto properties = D2D1::RenderTargetProperties(
			D2D1_RENDER_TARGET_TYPE_DEFAULT,
			D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
		result = d2dFactory_->CreateDCRenderTarget(&properties, inputRenderTarget_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	inputRenderTarget_->SetDpi(static_cast<float>(inputDpi_), static_cast<float>(inputDpi_));
	if (!inputTextFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, config_.fontSize, L"", inputTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		inputTextFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		inputTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		inputTextFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
	}
	if (!inputBorderBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.borderColor), inputBorderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!inputBackgroundBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.backgroundColor), inputBackgroundBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!inputTextBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.textColor), inputTextBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!inputPlaceholderBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(PlaceholderColorFromArgb(config_.textColor), inputPlaceholderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!inputCaretBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.caretColor), inputCaretBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

HRESULT InputBoxHost::EnsureFeatureResources()
{
	auto result = EnsureFactories();
	if (FAILED(result)) return result;
	const auto width = GetFeatureWindowPixelWidth();
	const auto height = GetFeatureWindowPixelHeight();
	if (featureSurface_ == nullptr)
	{
		featureSurface_ = std::make_unique<CachedSurface>();
	}
	result = featureSurface_->Ensure(width, height);
	if (FAILED(result)) return result;
	if (!featureRenderTarget_)
	{
		const auto properties = D2D1::RenderTargetProperties(
			D2D1_RENDER_TARGET_TYPE_DEFAULT,
			D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
		result = d2dFactory_->CreateDCRenderTarget(&properties, featureRenderTarget_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	featureRenderTarget_->SetDpi(static_cast<float>(featureDpi_), static_cast<float>(featureDpi_));
	if (!featureTextFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, featureConfig_.fontSize, L"", featureTextFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		featureTextFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
		featureTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		featureTextFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_WHOLE_WORD);
	}
	if (!featureNumberFormat_)
	{
		result = dwriteFactory_->CreateTextFormat(
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL, featureConfig_.fontSize * 1.55f, L"", featureNumberFormat_.GetAddressOf());
		if (FAILED(result)) return result;
		featureNumberFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
		featureNumberFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
		featureNumberFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
	}
	if (!featureBorderBrush_)
	{
		result = featureRenderTarget_->CreateSolidColorBrush(ColorFromArgb(featureConfig_.borderColor), featureBorderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!featureBackgroundBrush_)
	{
		result = featureRenderTarget_->CreateSolidColorBrush(ColorFromArgb(featureConfig_.backgroundColor), featureBackgroundBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!featureTextBrush_)
	{
		result = featureRenderTarget_->CreateSolidColorBrush(ColorFromArgb(featureConfig_.textColor), featureTextBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!featureAccentBrush_)
	{
		result = featureRenderTarget_->CreateSolidColorBrush(ColorFromArgb(featureConfig_.accentColor), featureAccentBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

void InputBoxHost::DiscardInputResources(bool discardSurface)
{
	inputCaretBrush_.Reset();
	inputPlaceholderBrush_.Reset();
	inputTextBrush_.Reset();
	inputBorderBrush_.Reset();
	inputBackgroundBrush_.Reset();
	inputTextFormat_.Reset();
	inputRenderTarget_.Reset();
	if (discardSurface && inputSurface_ != nullptr)
	{
		inputSurface_->Reset();
	}
}

void InputBoxHost::DiscardFeatureResources(bool discardSurface)
{
	featureAccentBrush_.Reset();
	featureTextBrush_.Reset();
	featureBorderBrush_.Reset();
	featureBackgroundBrush_.Reset();
	featureNumberFormat_.Reset();
	featureTextFormat_.Reset();
	featureRenderTarget_.Reset();
	if (discardSurface && featureSurface_ != nullptr)
	{
		featureSurface_->Reset();
	}
}

void InputBoxHost::DiscardAllResources()
{
	DiscardInputResources(true);
	DiscardFeatureResources(true);
	inputSurface_.reset();
	featureSurface_.reset();
	dwriteFactory_.Reset();
	d2dFactory_.Reset();
}

void InputBoxHost::ApplyConfigOnUiThread(const LuvLetterInputBoxConfig& config)
{
	config_ = SanitizeConfig(config);
	horizontalOffset_ = 0.0f;
	DiscardInputResources(true);
	UpdateInputWindowShape();
	UpdateInputWindowPosition();
	if (inputVisible_) RenderInput();
}

void InputBoxHost::ApplyFeatureConfigOnUiThread(const LuvLetterFeatureWindowConfig& config)
{
	featureConfig_ = SanitizeFeatureConfig(config);
	const auto pageCount = GetFeaturePageCount();
	featurePage_ = pageCount == 0 ? 0 : (std::min)(featurePage_, pageCount - 1);
	DiscardFeatureResources(true);
	UpdateFeatureWindowGeometry();
	if (featureVisible_) RenderFeature();
}

void InputBoxHost::SetFeatureItemsOnUiThread(std::vector<FeatureItem>&& items)
{
	featureItems_ = std::move(items);
	featurePage_ = 0;
	if (featureItems_.empty())
	{
		HideFeatureWindowOnUiThread();
	}
	DiscardFeatureResources(true);
	UpdateFeatureWindowGeometry();
	if (featureVisible_) RenderFeature();
}

void InputBoxHost::UpdateInputWindowShape() const
{
	if (inputHwnd_ != nullptr)
	{
		// HRGN coverage is binary and destroys the per-pixel antialiasing produced
		// by D2D. The layered bitmap already supplies the visual and hit-test shape.
		SetWindowRgn(inputHwnd_, nullptr, TRUE);
	}
}

void InputBoxHost::UpdateFeatureWindowShape() const
{
	if (featureHwnd_ != nullptr)
	{
		// Alpha-zero gaps and corners of an UpdateLayeredWindow bitmap are already
		// transparent to hit testing; an integer HRGN would only reintroduce stairs.
		SetWindowRgn(featureHwnd_, nullptr, TRUE);
	}
}

HMONITOR InputBoxHost::CaptureTargetMonitor() const
{
	const auto foreground = GetForegroundWindow();
	if (foreground != nullptr && foreground != inputHwnd_ && foreground != featureHwnd_)
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

void InputBoxHost::RefreshInputDpiFromWindow()
{
	const auto dpi = QueryWindowDpi(inputHwnd_);
	if (dpi == inputDpi_) return;
	inputDpi_ = dpi;
	DiscardInputResources(true);
}

void InputBoxHost::RefreshFeatureDpiFromWindow()
{
	const auto dpi = QueryWindowDpi(featureHwnd_);
	if (dpi == featureDpi_) return;
	featureDpi_ = dpi;
	DiscardFeatureResources(true);
}

void InputBoxHost::ApplyInputDpiChange(UINT dpi, const RECT* suggestedRect)
{
	inputDpi_ = NormalizeDpi(dpi);
	DiscardInputResources(true);
	if (suggestedRect != nullptr)
	{
		targetMonitor_ = MonitorFromRect(suggestedRect, MONITOR_DEFAULTTONEAREST);
		SetWindowPos(
			inputHwnd_, nullptr,
			suggestedRect->left, suggestedRect->top,
			suggestedRect->right - suggestedRect->left,
			suggestedRect->bottom - suggestedRect->top,
			SWP_NOZORDER | SWP_NOACTIVATE);
	}
	else
	{
		targetMonitor_ = MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTONEAREST);
		UpdateInputWindowPosition();
	}
	UpdateInputWindowShape();
	UpdateImeCompositionWindow();
	if (inputVisible_) RenderInput();
}

void InputBoxHost::ApplyFeatureDpiChange(UINT dpi, const RECT* suggestedRect)
{
	featureDpi_ = NormalizeDpi(dpi);
	DiscardFeatureResources(true);
	if (suggestedRect != nullptr)
	{
		targetMonitor_ = MonitorFromRect(suggestedRect, MONITOR_DEFAULTTONEAREST);
		SetWindowPos(
			featureHwnd_, nullptr,
			suggestedRect->left, suggestedRect->top,
			suggestedRect->right - suggestedRect->left,
			suggestedRect->bottom - suggestedRect->top,
			SWP_NOZORDER | SWP_NOACTIVATE);
	}
	else
	{
		targetMonitor_ = MonitorFromWindow(featureHwnd_, MONITOR_DEFAULTTONEAREST);
		UpdateFeatureWindowPosition();
	}
	UpdateFeatureWindowShape();
	if (featureVisible_) RenderFeature();
}

void InputBoxHost::ShowInputWindowAndFocus()
{
	if (inputHwnd_ == nullptr) return;
	targetMonitor_ = CaptureTargetMonitor();
	HideFeatureWindowOnUiThread();
	ResetInput();
	caretVisible_ = true;
	// The first move enters the target monitor and lets PMv2 deliver authoritative
	// WM_DPICHANGED data. GetDpiForWindow then verifies it before final placement.
	UpdateInputWindowPosition();
	RefreshInputDpiFromWindow();
	UpdateInputWindowPosition();
	UpdateInputWindowShape();
	inputVisible_ = true;
	ShowWindow(inputHwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(inputHwnd_);
	SetFocus(inputHwnd_);
	SetTimer(inputHwnd_, CaretTimerId, CaretBlinkMs, nullptr);
	RenderInput();
}

void InputBoxHost::HideInputWindow()
{
	if (inputHwnd_ == nullptr) return;
	inputVisible_ = false;
	KillTimer(inputHwnd_, CaretTimerId);
	ShowWindow(inputHwnd_, SW_HIDE);
}

void InputBoxHost::ShowFeatureWindowAndFocus()
{
	if (featureHwnd_ == nullptr || featureItems_.empty()) return;
	targetMonitor_ = CaptureTargetMonitor();
	HideInputWindow();
	featurePage_ = (std::min)(featurePage_, GetFeaturePageCount() - 1);
	// See ShowInputWindowAndFocus: move once to obtain the window's target DPI,
	// then perform the configured DIP-based placement with that DPI.
	UpdateFeatureWindowPosition();
	RefreshFeatureDpiFromWindow();
	UpdateFeatureWindowGeometry();
	featureVisible_ = true;
	ShowWindow(featureHwnd_, SW_SHOWNORMAL);
	SetForegroundWindow(featureHwnd_);
	SetFocus(featureHwnd_);
	RenderFeature();
}

void InputBoxHost::HideFeatureWindowOnUiThread()
{
	if (featureHwnd_ == nullptr) return;
	featureVisible_ = false;
	ShowWindow(featureHwnd_, SW_HIDE);
}

void InputBoxHost::UpdateInputWindowPosition() const
{
	if (inputHwnd_ == nullptr) return;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTOPRIMARY);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return;
	const auto width = GetInputWindowPixelWidth();
	const auto height = GetInputWindowPixelHeight();
	const auto bottomMargin = DipToPixels(static_cast<float>(config_.bottomMargin), inputDpi_);
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
		x = monitorInfo.rcWork.left + DipToPixels(static_cast<float>(config_.customX), inputDpi_);
		y = monitorInfo.rcWork.top + DipToPixels(static_cast<float>(config_.customY), inputDpi_);
		break;
	default:
		break;
	}
	x += DipToPixels(static_cast<float>(config_.offsetX), inputDpi_);
	y += DipToPixels(static_cast<float>(config_.offsetY), inputDpi_);
	SetWindowPos(inputHwnd_, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
}

void InputBoxHost::UpdateFeatureWindowGeometry()
{
	if (featureHwnd_ == nullptr) return;
	UpdateFeatureWindowShape();
	UpdateFeatureWindowPosition();
}

void InputBoxHost::UpdateFeatureWindowPosition() const
{
	if (featureHwnd_ == nullptr) return;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: MonitorFromWindow(featureHwnd_, MONITOR_DEFAULTTOPRIMARY);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return;
	const auto width = GetFeatureWindowPixelWidth();
	const auto height = GetFeatureWindowPixelHeight();
	const auto workWidth = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
	LONG x = monitorInfo.rcWork.left + ((std::max)(0L, workWidth - static_cast<LONG>(width)) / 2);
	LONG y = monitorInfo.rcWork.bottom - height
		- DipToPixels(static_cast<float>(featureConfig_.bottomMargin), featureDpi_);
	x += DipToPixels(static_cast<float>(featureConfig_.offsetX), featureDpi_);
	y += DipToPixels(static_cast<float>(featureConfig_.offsetY), featureDpi_);
	SetWindowPos(featureHwnd_, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
}

void InputBoxHost::ResetInput()
{
	text_.clear();
	caretIndex_ = 0;
	horizontalOffset_ = 0.0f;
	historyIndex_ = -1;
	historyDraft_.clear();
}

void InputBoxHost::SubmitInput()
{
	if (!text_.empty())
	{
		InvokeInputCallback(
			inputSubmittedCallback_,
			text_.c_str(),
			static_cast<int32_t>((std::min)(text_.size(), static_cast<size_t>((std::numeric_limits<int32_t>::max)()))),
			inputSubmittedContext_);
		if (history_.empty() || history_.back() != text_)
		{
			history_.push_back(text_);
			if (history_.size() > MaxHistoryItems)
			{
				history_.erase(history_.begin());
			}
		}
	}
	historyIndex_ = -1;
	historyDraft_.clear();
	text_.clear();
	caretIndex_ = 0;
	horizontalOffset_ = 0.0f;
	InvalidateInput();
}

void InputBoxHost::InsertText(const std::wstring& value)
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
	caretIndex_ += safeCount;
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::InsertCharacter(wchar_t value)
{
	if (text_.size() >= MaxInputCharacters) return;
	// WM_CHAR delivers a supplementary character as two UTF-16 code units. Do not
	// admit the high surrogate into the final slot, where its low surrogate could
	// no longer be appended. Likewise, ignore an unmatched low surrogate.
	if (IsHighSurrogate(value) && MaxInputCharacters - text_.size() < 2) return;
	if (IsLowSurrogate(value)
		&& (caretIndex_ == 0 || !IsHighSurrogate(text_[caretIndex_ - 1]))) return;
	text_.insert(caretIndex_, 1, value);
	++caretIndex_;
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::DeleteBeforeCaret()
{
	if (caretIndex_ == 0 || text_.empty()) return;
	const auto previous = PreviousUtf16Boundary(text_, caretIndex_);
	text_.erase(previous, caretIndex_ - previous);
	caretIndex_ = previous;
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::DeleteAtCaret()
{
	if (caretIndex_ >= text_.size()) return;
	const auto next = NextUtf16Boundary(text_, caretIndex_);
	text_.erase(caretIndex_, next - caretIndex_);
	historyIndex_ = -1;
	historyDraft_.clear();
	InvalidateInput();
}

void InputBoxHost::MoveCaretLeft()
{
	if (caretIndex_ == 0) return;
	caretIndex_ = PreviousUtf16Boundary(text_, caretIndex_);
	InvalidateInput();
}

void InputBoxHost::MoveCaretRight()
{
	if (caretIndex_ >= text_.size()) return;
	caretIndex_ = NextUtf16Boundary(text_, caretIndex_);
	InvalidateInput();
}

void InputBoxHost::MoveCaretToStart()
{
	if (caretIndex_ == 0) return;
	caretIndex_ = 0;
	InvalidateInput();
}

void InputBoxHost::MoveCaretToEnd()
{
	if (caretIndex_ == text_.size()) return;
	caretIndex_ = text_.size();
	InvalidateInput();
}

void InputBoxHost::NavigateHistory(int direction)
{
	if (history_.empty()) return;
	if (historyIndex_ < 0)
	{
		if (direction > 0) return;
		historyDraft_ = text_;
		historyIndex_ = static_cast<int>(history_.size()) - 1;
	}
	else
	{
		historyIndex_ += direction;
	}
	if (historyIndex_ < 0) historyIndex_ = 0;
	if (historyIndex_ >= static_cast<int>(history_.size()))
	{
		historyIndex_ = -1;
		text_ = historyDraft_;
		historyDraft_.clear();
	}
	else
	{
		text_ = history_[historyIndex_];
	}
	caretIndex_ = text_.size();
	InvalidateInput();
}

void InputBoxHost::PasteFromClipboard()
{
	if (inputHwnd_ == nullptr || !OpenClipboard(inputHwnd_)) return;
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

void InputBoxHost::SetCaretFromPoint(LPARAM lParam)
{
	if (text_.empty() || FAILED(EnsureInputResources()))
	{
		caretIndex_ = text_.size();
		InvalidateInput();
		return;
	}
	const auto horizontalPadding = (std::min)(config_.horizontalPadding, static_cast<float>(config_.width) / 2.0f - 1.0f);
	const auto verticalPadding = (std::min)(config_.verticalPadding, static_cast<float>(config_.height) / 2.0f - 1.0f);
	Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
	if (FAILED(dwriteFactory_->CreateTextLayout(
		text_.c_str(), static_cast<UINT32>(text_.size()), inputTextFormat_.Get(),
		MaxTextLayoutWidth, config_.height - 2.0f * verticalPadding, layout.GetAddressOf()))) return;
	BOOL trailing = FALSE;
	BOOL inside = FALSE;
	DWRITE_HIT_TEST_METRICS metrics{};
	const auto x = PixelsToDip(GET_X_LPARAM(lParam), inputDpi_)
		- horizontalPadding + horizontalOffset_;
	const auto y = PixelsToDip(GET_Y_LPARAM(lParam), inputDpi_) - verticalPadding;
	if (SUCCEEDED(layout->HitTestPoint(x, y, &trailing, &inside, &metrics)))
	{
		caretIndex_ = (std::min)(
			static_cast<size_t>(metrics.textPosition + (trailing ? metrics.length : 0)),
			text_.size());
		if (caretIndex_ > 0 && caretIndex_ < text_.size()
			&& IsHighSurrogate(text_[caretIndex_ - 1]) && IsLowSurrogate(text_[caretIndex_]))
		{
			caretIndex_ = trailing ? caretIndex_ + 1 : caretIndex_ - 1;
		}
		InvalidateInput();
	}
}

void InputBoxHost::InvalidateInput()
{
	caretVisible_ = true;
	EnsureCaretVisible();
	if (inputHwnd_ != nullptr)
	{
		UpdateImeCompositionWindow();
		SetTimer(inputHwnd_, CaretTimerId, CaretBlinkMs, nullptr);
		RenderInput();
	}
}

float InputBoxHost::GetCaretLogicalX()
{
	if (text_.empty() || caretIndex_ == 0 || FAILED(EnsureInputResources())) return 0.0f;
	Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
	if (FAILED(dwriteFactory_->CreateTextLayout(
		text_.c_str(), static_cast<UINT32>(text_.size()), inputTextFormat_.Get(),
		MaxTextLayoutWidth, static_cast<float>(config_.height), layout.GetAddressOf()))) return 0.0f;
	DWRITE_HIT_TEST_METRICS metrics{};
	float x = 0.0f;
	float y = 0.0f;
	if (SUCCEEDED(layout->HitTestTextPosition(
		static_cast<UINT32>((std::min)(caretIndex_, text_.size())), FALSE, &x, &y, &metrics)))
	{
		return x;
	}
	return 0.0f;
}

void InputBoxHost::EnsureCaretVisible()
{
	const auto availableWidth = (std::max)(1.0f, static_cast<float>(config_.width) - 2.0f * config_.horizontalPadding);
	const auto caretX = GetCaretLogicalX();
	const auto margin = (std::max)(config_.caretWidth + 2.0f, 4.0f);
	if (caretX - horizontalOffset_ > availableWidth - margin)
	{
		horizontalOffset_ = caretX - availableWidth + margin;
	}
	else if (caretX < horizontalOffset_)
	{
		horizontalOffset_ = caretX;
	}
	horizontalOffset_ = (std::max)(0.0f, horizontalOffset_);
}

void InputBoxHost::UpdateImeCompositionWindow()
{
	if (inputHwnd_ == nullptr || FAILED(EnsureInputResources())) return;
	const auto inputContext = ImmGetContext(inputHwnd_);
	if (inputContext == nullptr) return;
	const auto horizontalPadding = (std::max)(0.0f, config_.horizontalPadding);
	const auto verticalPadding = (std::max)(0.0f, config_.verticalPadding);
	COMPOSITIONFORM compositionForm{};
	compositionForm.dwStyle = CFS_POINT;
	compositionForm.ptCurrentPos.x = DipToPixels(
		horizontalPadding + GetCaretLogicalX() - horizontalOffset_,
		inputDpi_);
	compositionForm.ptCurrentPos.y = DipToPixels(
		verticalPadding + config_.fontSize,
		inputDpi_);
	ImmSetCompositionWindow(inputContext, &compositionForm);
	ImmReleaseContext(inputHwnd_, inputContext);
}

void InputBoxHost::RenderInput()
{
	if (inputHwnd_ == nullptr || FAILED(EnsureInputResources())) return;
	const auto width = GetInputWindowPixelWidth();
	const auto height = GetInputWindowPixelHeight();
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(inputRenderTarget_->BindDC(inputSurface_->dc, &bindRect))) return;
	inputRenderTarget_->BeginDraw();
	inputRenderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	inputRenderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto rounded = CreateInsetRoundedRect(
		0.0f,
		0.0f,
		static_cast<float>(config_.width),
		static_cast<float>(config_.height),
		config_.cornerRadius,
		config_.borderThickness);
	inputRenderTarget_->FillRoundedRectangle(rounded, inputBackgroundBrush_.Get());
	if (config_.borderThickness > 0.0f)
	{
		inputRenderTarget_->DrawRoundedRectangle(
			rounded, inputBorderBrush_.Get(), config_.borderThickness);
	}
	const auto horizontalPadding = (std::min)((std::max)(0.0f, config_.horizontalPadding), config_.width / 2.0f - 1.0f);
	const auto verticalPadding = (std::min)((std::max)(0.0f, config_.verticalPadding), config_.height / 2.0f - 1.0f);
	const auto textRect = D2D1::RectF(
		horizontalPadding, verticalPadding,
		config_.width - horizontalPadding, config_.height - verticalPadding);
	inputRenderTarget_->PushAxisAlignedClip(textRect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	if (text_.empty())
	{
		inputRenderTarget_->DrawTextW(
			PlaceholderText, static_cast<UINT32>(std::size(PlaceholderText) - 1), inputTextFormat_.Get(),
			textRect, inputPlaceholderBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
	}
	else
	{
		Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
		if (SUCCEEDED(dwriteFactory_->CreateTextLayout(
			text_.c_str(), static_cast<UINT32>(text_.size()), inputTextFormat_.Get(),
			MaxTextLayoutWidth, textRect.bottom - textRect.top, layout.GetAddressOf())))
		{
			inputRenderTarget_->DrawTextLayout(
				D2D1::Point2F(textRect.left - horizontalOffset_, textRect.top),
				layout.Get(), inputTextBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
		}
	}
	if (caretVisible_)
	{
		const auto caretX = textRect.left + GetCaretLogicalX() - horizontalOffset_;
		const auto textHeight = (std::max)(1.0f, textRect.bottom - textRect.top);
		const auto caretHeight = (std::min)(textHeight, (std::max)(1.0f, config_.fontSize * 1.1f));
		const auto caretTop = textRect.top + (textHeight - caretHeight) / 2.0f;
		inputRenderTarget_->FillRectangle(
			D2D1::RectF(caretX, caretTop, caretX + config_.caretWidth, caretTop + caretHeight),
			inputCaretBrush_.Get());
	}
	inputRenderTarget_->PopAxisAlignedClip();
	const auto endResult = inputRenderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardInputResources(false);
		return;
	}
	if (SUCCEEDED(endResult))
	{
		POINT source{ 0, 0 };
		SIZE size{ width, height };
		BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
		UpdateLayeredWindow(inputHwnd_, nullptr, nullptr, &size, inputSurface_->dc, &source, 0, &blend, ULW_ALPHA);
	}
}

size_t InputBoxHost::GetFeaturePageCount() const
{
	if (featureItems_.empty()) return 0;
	const auto perPage = static_cast<size_t>((std::max)(1, featureConfig_.itemsPerPage));
	return (featureItems_.size() + perPage - 1) / perPage;
}

size_t InputBoxHost::GetFeaturePageItemCount() const
{
	if (featureItems_.empty()) return 0;
	const auto perPage = static_cast<size_t>((std::max)(1, featureConfig_.itemsPerPage));
	const auto start = featurePage_ * perPage;
	return start >= featureItems_.size() ? 0 : (std::min)(perPage, featureItems_.size() - start);
}

float InputBoxHost::GetFeatureWindowWidthDip() const
{
	const auto count = (std::max)(size_t{ 1 }, GetFeaturePageItemCount());
	return (std::max)(1.0f,
		static_cast<float>(count) * featureConfig_.cellSize
		+ static_cast<float>(count - 1) * featureConfig_.gap);
}

int InputBoxHost::GetInputWindowPixelWidth() const
{
	return (std::max)(1, DipToPixels(static_cast<float>(config_.width), inputDpi_));
}

int InputBoxHost::GetInputWindowPixelHeight() const
{
	return (std::max)(1, DipToPixels(static_cast<float>(config_.height), inputDpi_));
}

int InputBoxHost::GetFeatureWindowPixelWidth() const
{
	return (std::max)(1, DipToPixels(GetFeatureWindowWidthDip(), featureDpi_));
}

int InputBoxHost::GetFeatureWindowPixelHeight() const
{
	return (std::max)(1, DipToPixels(featureConfig_.cellSize, featureDpi_));
}

void InputBoxHost::RenderFeature()
{
	if (featureHwnd_ == nullptr || featureItems_.empty() || FAILED(EnsureFeatureResources())) return;
	const auto width = GetFeatureWindowPixelWidth();
	const auto height = GetFeatureWindowPixelHeight();
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(featureRenderTarget_->BindDC(featureSurface_->dc, &bindRect))) return;
	featureRenderTarget_->BeginDraw();
	featureRenderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	featureRenderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto count = GetFeaturePageItemCount();
	const auto perPage = static_cast<size_t>(featureConfig_.itemsPerPage);
	const auto start = featurePage_ * perPage;
	for (size_t index = 0; index < count; ++index)
	{
		const auto left = static_cast<float>(index) * (featureConfig_.cellSize + featureConfig_.gap);
		const auto rounded = CreateInsetRoundedRect(
			left,
			0.0f,
			left + featureConfig_.cellSize,
			featureConfig_.cellSize,
			featureConfig_.cornerRadius,
			featureConfig_.borderThickness);
		featureRenderTarget_->FillRoundedRectangle(rounded, featureBackgroundBrush_.Get());
		if (featureConfig_.borderThickness > 0.0f)
		{
			featureRenderTarget_->DrawRoundedRectangle(
				rounded, featureBorderBrush_.Get(), featureConfig_.borderThickness);
		}
		const wchar_t number[] = {
			static_cast<wchar_t>(featureConfig_.firstItemVirtualKey + index),
			L'\0'
		};
		const auto inset = (std::max)(6.0f, featureConfig_.cellSize * 0.08f);
		const auto numberRect = D2D1::RectF(
			left + inset, inset,
			left + featureConfig_.cellSize - inset, featureConfig_.cellSize * 0.52f);
		const auto labelRect = D2D1::RectF(
			left + inset, featureConfig_.cellSize * 0.42f,
			left + featureConfig_.cellSize - inset, featureConfig_.cellSize - inset);
		featureRenderTarget_->DrawTextW(number, 1, featureNumberFormat_.Get(), numberRect, featureAccentBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
		const auto& label = featureItems_[start + index].label;
		featureRenderTarget_->DrawTextW(
			label.c_str(), static_cast<UINT32>(label.size()), featureTextFormat_.Get(), labelRect,
			featureTextBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
	}
	const auto endResult = featureRenderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardFeatureResources(false);
		return;
	}
	if (SUCCEEDED(endResult))
	{
		POINT source{ 0, 0 };
		SIZE size{ width, height };
		BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
		UpdateLayeredWindow(featureHwnd_, nullptr, nullptr, &size, featureSurface_->dc, &source, 0, &blend, ULW_ALPHA);
	}
}

bool InputBoxHost::HandleInputKeyDown(WPARAM wParam)
{
	if ((wParam == L'V' || wParam == L'v') && IsKeyDown(VK_CONTROL))
	{
		PasteFromClipboard();
		return true;
	}
	if (MatchesHotkey(wParam, config_.cancelVirtualKey, config_.cancelModifiers))
	{
		HideInputWindow();
		return true;
	}
	if (MatchesHotkey(wParam, config_.submitVirtualKey, config_.submitModifiers))
	{
		SubmitInput();
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

bool InputBoxHost::HandleFeatureKeyDown(WPARAM wParam)
{
	if (MatchesHotkey(wParam, featureConfig_.cancelVirtualKey, featureConfig_.cancelModifiers))
	{
		HideFeatureWindowOnUiThread();
		return true;
	}
	if (MatchesHotkey(wParam, featureConfig_.previousVirtualKey, featureConfig_.previousModifiers))
	{
		ChangeFeaturePage(-1);
		return true;
	}
	if (MatchesHotkey(wParam, featureConfig_.nextVirtualKey, featureConfig_.nextModifiers))
	{
		ChangeFeaturePage(1);
		return true;
	}
	if (GetCurrentHotkeyModifiers() == 0)
	{
		const auto firstKey = static_cast<WPARAM>(featureConfig_.firstItemVirtualKey);
		if (wParam >= firstKey && wParam < firstKey + static_cast<WPARAM>(featureConfig_.itemsPerPage))
		{
			ActivateFeature(static_cast<size_t>(wParam - firstKey));
			return true;
		}
		if (featureConfig_.firstItemVirtualKey == L'1' && wParam >= VK_NUMPAD1 && wParam <= VK_NUMPAD7)
		{
			ActivateFeature(static_cast<size_t>(wParam - VK_NUMPAD1));
			return true;
		}
	}
	return false;
}

void InputBoxHost::ChangeFeaturePage(int direction)
{
	const auto pageCount = GetFeaturePageCount();
	if (pageCount <= 1) return;
	if (direction < 0)
	{
		featurePage_ = featurePage_ == 0 ? pageCount - 1 : featurePage_ - 1;
	}
	else
	{
		featurePage_ = (featurePage_ + 1) % pageCount;
	}
	DiscardFeatureResources(true);
	UpdateFeatureWindowGeometry();
	RenderFeature();
}

void InputBoxHost::ActivateFeature(size_t indexOnPage)
{
	if (indexOnPage >= GetFeaturePageItemCount()) return;
	const auto absoluteIndex = featurePage_ * static_cast<size_t>(featureConfig_.itemsPerPage) + indexOnPage;
	const auto token = featureItems_[absoluteIndex].token;
	const auto callback = featureActivatedCallback_;
	const auto context = featureActivatedContext_;
	HideFeatureWindowOnUiThread();
	InvokeFeatureCallback(callback, token, context);
}

LRESULT InputBoxHost::HandleInputMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND: return 1;
	case WM_SETFOCUS:
		caretVisible_ = true;
		SetTimer(hwnd, CaretTimerId, CaretBlinkMs, nullptr);
		UpdateImeCompositionWindow();
		RenderInput();
		return 0;
	case WM_KILLFOCUS:
		caretVisible_ = false;
		KillTimer(hwnd, CaretTimerId);
		RenderInput();
		return 0;
	case WM_TIMER:
		if (wParam == CaretTimerId)
		{
			caretVisible_ = !caretVisible_;
			RenderInput();
			return 0;
		}
		break;
	case WM_KEYDOWN:
	case WM_SYSKEYDOWN:
		if (HandleInputKeyDown(wParam)) return 0;
		break;
	case WM_CHAR:
		if (wParam == L'\b' || wParam == L'\r' || wParam == L'\n' || wParam == L'\t') return 0;
		if (wParam >= 0x20) InsertCharacter(static_cast<wchar_t>(wParam));
		return 0;
	case WM_SYSCHAR:
		return 0;
	case WM_PASTE:
		PasteFromClipboard();
		return 0;
	case WM_LBUTTONDOWN:
		SetFocus(hwnd);
		SetCaretFromPoint(lParam);
		return 0;
	case WM_IME_STARTCOMPOSITION:
		UpdateImeCompositionWindow();
		break;
	case WM_IME_COMPOSITION:
		UpdateImeCompositionWindow();
		if ((lParam & GCS_RESULTSTR) != 0)
		{
			const auto inputContext = ImmGetContext(hwnd);
			if (inputContext != nullptr)
			{
				const auto byteCount = ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, nullptr, 0);
				if (byteCount > 0)
				{
					std::wstring result(static_cast<size_t>(byteCount) / sizeof(wchar_t), L'\0');
					ImmGetCompositionStringW(inputContext, GCS_RESULTSTR, result.data(), byteCount);
					InsertText(result);
				}
				ImmReleaseContext(hwnd, inputContext);
			}
			return 0;
		}
		break;
	case WM_DPICHANGED:
		ApplyInputDpiChange(
			static_cast<UINT>(LOWORD(wParam)),
			reinterpret_cast<const RECT*>(lParam));
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paint{};
		BeginPaint(hwnd, &paint);
		RenderInput();
		EndPaint(hwnd, &paint);
		return 0;
	}
	case WM_SIZE:
		RenderInput();
		return 0;
	case WM_CLOSE:
		HideInputWindow();
		return 0;
	case WM_DESTROY:
		KillTimer(hwnd, CaretTimerId);
		DiscardInputResources(true);
		inputHwnd_ = nullptr;
		inputVisible_ = false;
		return 0;
	default:
		break;
	}
	return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT InputBoxHost::HandleFeatureMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_ERASEBKGND: return 1;
	case WM_KEYDOWN:
	case WM_SYSKEYDOWN:
		if ((lParam & (1LL << 30)) != 0) return 0;
		if (HandleFeatureKeyDown(wParam)) return 0;
		break;
	case WM_SYSCHAR:
		return 0;
	case WM_LBUTTONDOWN:
	{
		SetFocus(hwnd);
		const auto x = PixelsToDip(GET_X_LPARAM(lParam), featureDpi_);
		const auto stride = featureConfig_.cellSize + featureConfig_.gap;
		if (stride > 0.0f)
		{
			const auto index = static_cast<size_t>((std::max)(0.0f, std::floor(x / stride)));
			const auto withinCell = x - static_cast<float>(index) * stride;
			if (withinCell <= featureConfig_.cellSize) ActivateFeature(index);
		}
		return 0;
	}
	case WM_DPICHANGED:
		ApplyFeatureDpiChange(
			static_cast<UINT>(LOWORD(wParam)),
			reinterpret_cast<const RECT*>(lParam));
		return 0;
	case WM_PAINT:
	{
		PAINTSTRUCT paint{};
		BeginPaint(hwnd, &paint);
		RenderFeature();
		EndPaint(hwnd, &paint);
		return 0;
	}
	case WM_SIZE:
		RenderFeature();
		return 0;
	case WM_CLOSE:
		HideFeatureWindowOnUiThread();
		return 0;
	case WM_DESTROY:
		DiscardFeatureResources(true);
		featureHwnd_ = nullptr;
		featureVisible_ = false;
		return 0;
	default:
		break;
	}
	return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT InputBoxHost::DispatchWindowMessage(
	WindowKind kind,
	HWND hwnd,
	UINT message,
	WPARAM wParam,
	LPARAM lParam)
{
	return kind == WindowKind::Input
		? HandleInputMessage(hwnd, message, wParam, lParam)
		: HandleFeatureMessage(hwnd, message, wParam, lParam);
}

LuvLetterInputBoxConfig InputBoxHost::SanitizeConfig(const LuvLetterInputBoxConfig& config)
{
	auto sanitized = CreateDefaultConfig();
	sanitized.width = (std::clamp)(config.width, 120, 7680);
	sanitized.height = (std::clamp)(config.height, 24, 512);
	sanitized.cornerRadius = (std::clamp)(FiniteOr(config.cornerRadius, sanitized.cornerRadius), 0.0f, 512.0f);
	sanitized.borderThickness = (std::clamp)(
		FiniteOr(config.borderThickness, sanitized.borderThickness),
		0.0f,
		(std::min)(16.0f, static_cast<float>(sanitized.height) / 2.0f));
	sanitized.fontSize = (std::clamp)(FiniteOr(config.fontSize, sanitized.fontSize), 6.0f, 256.0f);
	sanitized.horizontalPadding = (std::clamp)(FiniteOr(config.horizontalPadding, sanitized.horizontalPadding), 0.0f, static_cast<float>(sanitized.width) / 2.0f);
	sanitized.verticalPadding = (std::clamp)(FiniteOr(config.verticalPadding, sanitized.verticalPadding), 0.0f, static_cast<float>(sanitized.height) / 2.0f);
	sanitized.caretWidth = (std::clamp)(FiniteOr(config.caretWidth, sanitized.caretWidth), 0.5f, 16.0f);
	sanitized.positionMode = config.positionMode >= 0 && config.positionMode <= 3 ? config.positionMode : sanitized.positionMode;
	sanitized.offsetX = (std::clamp)(config.offsetX, -32768, 32768);
	sanitized.offsetY = (std::clamp)(config.offsetY, -32768, 32768);
	sanitized.bottomMargin = (std::clamp)(config.bottomMargin, 0, 4096);
	sanitized.customX = (std::clamp)(config.customX, -32768, 32768);
	sanitized.customY = (std::clamp)(config.customY, -32768, 32768);
	sanitized.borderColor = config.borderColor;
	sanitized.backgroundColor = config.backgroundColor;
	sanitized.textColor = config.textColor;
	sanitized.caretColor = config.caretColor;
	sanitized.submitVirtualKey = config.submitVirtualKey > 0 && config.submitVirtualKey <= 0xFF ? config.submitVirtualKey : sanitized.submitVirtualKey;
	sanitized.cancelVirtualKey = config.cancelVirtualKey > 0 && config.cancelVirtualKey <= 0xFF ? config.cancelVirtualKey : sanitized.cancelVirtualKey;
	sanitized.backspaceVirtualKey = config.backspaceVirtualKey > 0 && config.backspaceVirtualKey <= 0xFF ? config.backspaceVirtualKey : sanitized.backspaceVirtualKey;
	sanitized.submitModifiers = config.submitModifiers & 0xF;
	sanitized.cancelModifiers = config.cancelModifiers & 0xF;
	sanitized.backspaceModifiers = config.backspaceModifiers & 0xF;
	return sanitized;
}

LuvLetterFeatureWindowConfig InputBoxHost::SanitizeFeatureConfig(const LuvLetterFeatureWindowConfig& config)
{
	auto sanitized = CreateDefaultFeatureConfig();
	sanitized.itemsPerPage = (std::clamp)(config.itemsPerPage, 1, 7);
	sanitized.cellSize = (std::clamp)(FiniteOr(config.cellSize, sanitized.cellSize), 32.0f, 512.0f);
	sanitized.gap = (std::clamp)(FiniteOr(config.gap, sanitized.gap), 0.0f, 128.0f);
	sanitized.cornerRadius = (std::clamp)(FiniteOr(config.cornerRadius, sanitized.cornerRadius), 0.0f, 256.0f);
	sanitized.borderThickness = (std::clamp)(
		FiniteOr(config.borderThickness, sanitized.borderThickness),
		0.0f,
		(std::min)(16.0f, sanitized.cellSize / 2.0f));
	sanitized.fontSize = (std::clamp)(FiniteOr(config.fontSize, sanitized.fontSize), 6.0f, 128.0f);
	sanitized.bottomMargin = (std::clamp)(config.bottomMargin, 0, 4096);
	sanitized.offsetX = (std::clamp)(config.offsetX, -32768, 32768);
	sanitized.offsetY = (std::clamp)(config.offsetY, -32768, 32768);
	sanitized.borderColor = config.borderColor;
	sanitized.backgroundColor = config.backgroundColor;
	sanitized.textColor = config.textColor;
	sanitized.accentColor = config.accentColor;
	sanitized.previousVirtualKey = config.previousVirtualKey > 0 && config.previousVirtualKey <= 0xFF ? config.previousVirtualKey : sanitized.previousVirtualKey;
	sanitized.nextVirtualKey = config.nextVirtualKey > 0 && config.nextVirtualKey <= 0xFF ? config.nextVirtualKey : sanitized.nextVirtualKey;
	sanitized.cancelVirtualKey = config.cancelVirtualKey > 0 && config.cancelVirtualKey <= 0xFF ? config.cancelVirtualKey : sanitized.cancelVirtualKey;
	const auto maximumFirstItemKey = L'9' - sanitized.itemsPerPage + 1;
	sanitized.firstItemVirtualKey = config.firstItemVirtualKey >= L'0'
		&& config.firstItemVirtualKey <= maximumFirstItemKey
		? config.firstItemVirtualKey
		: sanitized.firstItemVirtualKey;
	sanitized.previousModifiers = config.previousModifiers & 0xF;
	sanitized.nextModifiers = config.nextModifiers & 0xF;
	sanitized.cancelModifiers = config.cancelModifiers & 0xF;
	return sanitized;
}

LRESULT InputBoxHost::DispatchWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept
{
	try
	{
		if (message == WM_NCCREATE)
		{
			const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
			auto* context = static_cast<WindowContext*>(createStruct->lpCreateParams);
			if (context == nullptr || context->host == nullptr) return FALSE;
			SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(context));
			return TRUE;
		}
		auto* context = reinterpret_cast<WindowContext*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
		if (context == nullptr || context->host == nullptr)
		{
			return DefWindowProcW(hwnd, message, wParam, lParam);
		}
		return context->host->DispatchWindowMessage(context->kind, hwnd, message, wParam, lParam);
	}
	catch (...)
	{
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}
}

LRESULT CALLBACK InputBoxHost::WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept
{
	__try
	{
		return DispatchWindowProc(hwnd, message, wParam, lParam);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return DefWindowProcW(hwnd, message, wParam, lParam);
	}
}
