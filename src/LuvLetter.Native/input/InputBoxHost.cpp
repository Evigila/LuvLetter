#include "input/InputBoxHost.h"
#include "input/NativeConfigurationSanitizer.h"

#include <imm.h>
#include <windowsx.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
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
	constexpr size_t MaxInputCharacters = 32768;
	constexpr int32_t MaxFeatureItems = 4096;
	constexpr float MaxTextLayoutHeight = 16777216.0f;
	constexpr int64_t MaxInputSurfacePixels = 16LL * 1024LL * 1024LL;
	constexpr int64_t MaxFeatureSurfacePixels = 16LL * 1024LL * 1024LL;
	constexpr wchar_t PlaceholderText[] = L"Enter command here";
	constexpr UINT DefaultDpi = 96;

	constexpr int SelectInputLineCapacity(UINT32 lineCount) noexcept
	{
		return lineCount <= 1 ? 1 : lineCount <= 2 ? 2 : lineCount <= 4 ? 4 : 6;
	}

	static_assert(SelectInputLineCapacity(1) == 1);
	static_assert(SelectInputLineCapacity(2) == 2);
	static_assert(SelectInputLineCapacity(3) == 4);
	static_assert(SelectInputLineCapacity(4) == 4);
	static_assert(SelectInputLineCapacity(5) == 6);
	static_assert(SelectInputLineCapacity(100) == 6);

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

	bool PresentLayeredSurface(
		HWND window,
		HDC sourceDc,
		int width,
		int height,
		const RECT* dirtyRect = nullptr) noexcept
	{
		POINT source{ 0, 0 };
		SIZE size{ width, height };
		BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
		if (dirtyRect != nullptr)
		{
			UPDATELAYEREDWINDOWINFO update{};
			update.cbSize = sizeof(update);
			update.psize = &size;
			update.hdcSrc = sourceDc;
			update.pptSrc = &source;
			update.pblend = &blend;
			update.dwFlags = ULW_ALPHA;
			update.prcDirty = dirtyRect;
			if (UpdateLayeredWindowIndirect(window, &update))
			{
				return true;
			}
		}

		return UpdateLayeredWindow(
			window,
			nullptr,
			nullptr,
			&size,
			sourceDc,
			&source,
			0,
			&blend,
			ULW_ALPHA) != FALSE;
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

	D2D1_COLOR_F ColorFromArgb(uint32_t argb)
	{
		return D2D1::ColorF(
			static_cast<float>((argb >> 16) & 0xFF) / 255.0f,
			static_cast<float>((argb >> 8) & 0xFF) / 255.0f,
			static_cast<float>(argb & 0xFF) / 255.0f,
			static_cast<float>((argb >> 24) & 0xFF) / 255.0f);
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
	void* bits = nullptr;
	int width = 0;
	int height = 0;

	~CachedSurface()
	{
		Reset();
	}

	HRESULT Ensure(int requestedWidth, int requestedHeight, int64_t maximumPixels)
	{
		if (requestedWidth <= 0 || requestedHeight <= 0 || maximumPixels <= 0)
		{
			return E_INVALIDARG;
		}
		if (static_cast<int64_t>(requestedWidth)
			> maximumPixels / static_cast<int64_t>(requestedHeight))
		{
			return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY);
		}
		const auto pixelCount = static_cast<int64_t>(requestedWidth)
			* static_cast<int64_t>(requestedHeight);
		if (pixelCount > static_cast<int64_t>((std::numeric_limits<DWORD>::max)()) / 4)
		{
			return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
		}
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
		bitmapInfo.bmiHeader.biSizeImage = static_cast<DWORD>(pixelCount * 4);
		bitmap = CreateDIBSection(
			screenDc,
			&bitmapInfo,
			DIB_RGB_COLORS,
			&bits,
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

	void Clear(const RECT& requestedRect) noexcept
	{
		if (bits == nullptr || width <= 0 || height <= 0)
		{
			return;
		}
		const auto left = (std::clamp)(requestedRect.left, 0L, static_cast<LONG>(width));
		const auto top = (std::clamp)(requestedRect.top, 0L, static_cast<LONG>(height));
		const auto right = (std::clamp)(requestedRect.right, left, static_cast<LONG>(width));
		const auto bottom = (std::clamp)(requestedRect.bottom, top, static_cast<LONG>(height));
		const auto stride = static_cast<size_t>(width) * 4U;
		const auto rowBytes = static_cast<size_t>(right - left) * 4U;
		auto* bytes = static_cast<unsigned char*>(bits);
		for (auto row = top; row < bottom; ++row)
		{
			std::memset(
				bytes + static_cast<size_t>(row) * stride + static_cast<size_t>(left) * 4U,
				0,
				rowBytes);
		}
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
		bits = nullptr;
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

	config_ = NativeConfigurationSanitizer::DefaultInputBox();
	featureConfig_ = NativeConfigurationSanitizer::DefaultFeatureWindow();
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
	featurePager_.Reset(0, static_cast<size_t>(featureConfig_.itemsPerPage));
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
	result = inputSurface_->Ensure(
		GetInputWindowPixelWidth(),
		GetInputWindowPixelHeight(),
		MaxInputSurfacePixels);
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
		inputTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
		inputTextFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
		result = inputTextFormat_->SetLineSpacing(
			DWRITE_LINE_SPACING_METHOD_UNIFORM,
			GetInputLineHeightDip(),
			config_.fontSize);
		if (FAILED(result)) return result;
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
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.textColor), inputPlaceholderBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!inputCaretBrush_)
	{
		result = inputRenderTarget_->CreateSolidColorBrush(ColorFromArgb(config_.caretColor), inputCaretBrush_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	if (!text_.empty() && !inputTextLayout_)
	{
		result = dwriteFactory_->CreateTextLayout(
			text_.c_str(), static_cast<UINT32>(text_.size()), inputTextFormat_.Get(),
			GetInputTextWidthDip(), MaxTextLayoutHeight, inputTextLayout_.GetAddressOf());
		if (FAILED(result)) return result;
	}
	return S_OK;
}

HRESULT InputBoxHost::EnsureFeatureResources()
{
	auto result = EnsureFactories();
	if (FAILED(result)) return result;
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetFeatureSurfaceMetrics(width, height, renderScale);
	(void)renderScale;
	if (featureSurface_ == nullptr)
	{
		featureSurface_ = std::make_unique<CachedSurface>();
	}
	result = featureSurface_->Ensure(width, height, MaxFeatureSurfacePixels);
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
			L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
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
	inputCaretDirtyValid_ = false;
	inputTextLayout_.Reset();
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
	config_ = NativeConfigurationSanitizer::SanitizeInputBox(config);
	inputLineCapacity_ = 1;
	verticalOffset_ = 0.0f;
	DiscardInputResources(true);
	UpdateResponsiveInputHeight();
	UpdateInputWindowShape();
	UpdateInputWindowPosition();
	if (inputVisible_) RenderInput();
}

void InputBoxHost::ApplyFeatureConfigOnUiThread(const LuvLetterFeatureWindowConfig& config)
{
	featureConfig_ = NativeConfigurationSanitizer::SanitizeFeatureWindow(config);
	featurePager_.SetItemsPerPage(static_cast<size_t>(featureConfig_.itemsPerPage));
	DiscardFeatureResources(true);
	UpdateFeatureWindowGeometry();
	if (featureVisible_) RenderFeature();
}

void InputBoxHost::SetFeatureItemsOnUiThread(std::vector<FeatureItem>&& items)
{
	featureItems_ = std::move(items);
	featurePager_.Reset(
		featureItems_.size(),
		static_cast<size_t>(featureConfig_.itemsPerPage));
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
	updatingFeatureWindowGeometry_ = true;
	UpdateFeatureWindowShape();
	UpdateFeatureWindowPosition();
	updatingFeatureWindowGeometry_ = false;
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
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetFeatureSurfaceMetrics(width, height, renderScale);
	(void)renderScale;
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
	inputTextLayout_.Reset();
	caretIndex_ = 0;
	inputLineCapacity_ = 1;
	verticalOffset_ = 0.0f;
	inputHistory_.ResetNavigation();
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
	}
	inputHistory_.Record(text_);
	text_.clear();
	inputTextLayout_.Reset();
	caretIndex_ = 0;
	verticalOffset_ = 0.0f;
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
	inputTextLayout_.Reset();
	caretIndex_ += safeCount;
	inputHistory_.ResetNavigation();
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
	inputTextLayout_.Reset();
	++caretIndex_;
	inputHistory_.ResetNavigation();
	InvalidateInput();
}

void InputBoxHost::DeleteBeforeCaret()
{
	if (caretIndex_ == 0 || text_.empty()) return;
	const auto previous = PreviousUtf16Boundary(text_, caretIndex_);
	text_.erase(previous, caretIndex_ - previous);
	inputTextLayout_.Reset();
	caretIndex_ = previous;
	inputHistory_.ResetNavigation();
	InvalidateInput();
}

void InputBoxHost::DeleteAtCaret()
{
	if (caretIndex_ >= text_.size()) return;
	const auto next = NextUtf16Boundary(text_, caretIndex_);
	text_.erase(caretIndex_, next - caretIndex_);
	inputTextLayout_.Reset();
	inputHistory_.ResetNavigation();
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
	if (!inputHistory_.TryNavigate(direction, text_)) return;
	inputTextLayout_.Reset();
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
	BOOL trailing = FALSE;
	BOOL inside = FALSE;
	DWRITE_HIT_TEST_METRICS metrics{};
	const auto x = PixelsToDip(GET_X_LPARAM(lParam), inputDpi_)
		- (std::max)(0.0f, config_.horizontalPadding);
	const auto textTop = GetInputTextTopDip();
	const auto y = PixelsToDip(GET_Y_LPARAM(lParam), inputDpi_) - textTop + verticalOffset_;
	if (SUCCEEDED(inputTextLayout_->HitTestPoint(x, y, &trailing, &inside, &metrics)))
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
	UpdateResponsiveInputHeight();
	EnsureCaretVisible();
	if (inputHwnd_ != nullptr)
	{
		UpdateImeCompositionWindow();
		SetTimer(inputHwnd_, CaretTimerId, CaretBlinkMs, nullptr);
		RenderInput();
	}
}

void InputBoxHost::UpdateResponsiveInputHeight()
{
	int nextCapacity = 1;
	if (!text_.empty())
	{
		if (FAILED(EnsureInputResources())) return;
		DWRITE_TEXT_METRICS metrics{};
		if (!inputTextLayout_ || FAILED(inputTextLayout_->GetMetrics(&metrics))) return;
		nextCapacity = SelectInputLineCapacity(metrics.lineCount);
	}

	if (nextCapacity == inputLineCapacity_) return;
	inputLineCapacity_ = nextCapacity;
	verticalOffset_ = 0.0f;
	DiscardInputResources(true);
	UpdateInputWindowPosition();
}

D2D1_POINT_2F InputBoxHost::GetCaretLogicalPosition()
{
	auto position = D2D1::Point2F(0.0f, 0.0f);
	if (text_.empty() || caretIndex_ == 0 || FAILED(EnsureInputResources())) return position;
	DWRITE_HIT_TEST_METRICS metrics{};
	if (inputTextLayout_ == nullptr || FAILED(inputTextLayout_->HitTestTextPosition(
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

void InputBoxHost::EnsureCaretVisible()
{
	if (text_.empty() || FAILED(EnsureInputResources()) || !inputTextLayout_)
	{
		verticalOffset_ = 0.0f;
		return;
	}
	const auto caret = GetCaretLogicalPosition();
	const auto lineHeight = GetInputLineHeightDip();
	const auto viewportHeight = GetInputTextViewportHeightDip();
	DWRITE_TEXT_METRICS textMetrics{};
	if (FAILED(inputTextLayout_->GetMetrics(&textMetrics))) return;
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

void InputBoxHost::UpdateImeCompositionWindow()
{
	if (inputHwnd_ == nullptr || FAILED(EnsureInputResources())) return;
	const auto inputContext = ImmGetContext(inputHwnd_);
	if (inputContext == nullptr) return;
	const auto horizontalPadding = (std::max)(0.0f, config_.horizontalPadding);
	const auto caret = GetCaretLogicalPosition();
	const auto textTop = GetInputTextTopDip();
	COMPOSITIONFORM compositionForm{};
	compositionForm.dwStyle = CFS_POINT;
	compositionForm.ptCurrentPos.x = DipToPixels(
		horizontalPadding + caret.x,
		inputDpi_);
	compositionForm.ptCurrentPos.y = DipToPixels(
		textTop + caret.y - verticalOffset_ + config_.fontSize,
		inputDpi_);
	ImmSetCompositionWindow(inputContext, &compositionForm);
	ImmReleaseContext(inputHwnd_, inputContext);
}

void InputBoxHost::RenderInput(bool caretOnly)
{
	if (inputHwnd_ == nullptr || FAILED(EnsureInputResources())) return;
	const auto width = GetInputWindowPixelWidth();
	const auto height = GetInputWindowPixelHeight();
	const auto horizontalPadding = (std::min)(
		(std::max)(0.0f, config_.horizontalPadding),
		config_.width / 2.0f - 1.0f);
	const auto windowHeight = GetInputWindowHeightDip();
	const auto verticalPadding = (std::min)(
		(std::max)(0.0f, config_.verticalPadding),
		windowHeight / 2.0f - 1.0f);
	const auto lineHeight = GetInputLineHeightDip();
	const auto textTop = GetInputTextTopDip();
	const auto textRect = D2D1::RectF(
		horizontalPadding, textTop,
		config_.width - horizontalPadding,
		(std::min)(windowHeight - verticalPadding, textTop + GetInputTextViewportHeightDip()));
	const auto caret = GetCaretLogicalPosition();
	const auto caretX = textRect.left + caret.x;
	const auto caretHeight = (std::min)(
		lineHeight,
		(std::max)(1.0f, config_.fontSize * 1.1f));
	const auto caretTop = textTop + caret.y - verticalOffset_
		+ (lineHeight - caretHeight) / 2.0f;
	RECT nextCaretDirty{
		DipToPixels(caretX, inputDpi_) - 2,
		DipToPixels(caretTop, inputDpi_) - 2,
		DipToPixels(caretX + config_.caretWidth, inputDpi_) + 2,
		DipToPixels(caretTop + caretHeight, inputDpi_) + 2,
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
	const auto dirtyMatches = inputCaretDirtyValid_ && nextCaretDirtyValid
		&& EqualRect(&inputCaretDirtyRect_, &nextCaretDirty) != FALSE;
	caretOnly = caretOnly && dirtyMatches;
	if (caretOnly)
	{
		inputSurface_->Clear(inputCaretDirtyRect_);
	}

	RECT bindRect{ 0, 0, width, height };
	if (FAILED(inputRenderTarget_->BindDC(inputSurface_->dc, &bindRect))) return;
	inputRenderTarget_->BeginDraw();
	inputRenderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	// Make the layered window's per-pixel alpha behavior explicit; ClearType is
	// not suitable for a premultiplied transparent render target.
	inputRenderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	if (caretOnly)
	{
		inputRenderTarget_->PushAxisAlignedClip(
			D2D1::RectF(
				PixelsToDip(inputCaretDirtyRect_.left, inputDpi_),
				PixelsToDip(inputCaretDirtyRect_.top, inputDpi_),
				PixelsToDip(inputCaretDirtyRect_.right, inputDpi_),
				PixelsToDip(inputCaretDirtyRect_.bottom, inputDpi_)),
			D2D1_ANTIALIAS_MODE_ALIASED);
	}
	else
	{
		inputRenderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	}
	const auto rounded = CreateInsetRoundedRect(
		0.0f,
		0.0f,
		static_cast<float>(config_.width),
		GetInputWindowHeightDip(),
		config_.cornerRadius,
		config_.borderThickness);
	inputRenderTarget_->FillRoundedRectangle(rounded, inputBackgroundBrush_.Get());
	if (config_.borderThickness > 0.0f)
	{
		inputRenderTarget_->DrawRoundedRectangle(
			rounded, inputBorderBrush_.Get(), config_.borderThickness);
	}
	inputRenderTarget_->PushAxisAlignedClip(textRect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	if (text_.empty())
	{
		const auto placeholderRect = D2D1::RectF(
			textRect.left, textTop, textRect.right, textTop + lineHeight);
		inputRenderTarget_->DrawTextW(
			PlaceholderText, static_cast<UINT32>(std::size(PlaceholderText) - 1), inputTextFormat_.Get(),
			placeholderRect, inputPlaceholderBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
	}
	else if (inputTextLayout_)
	{
		inputRenderTarget_->DrawTextLayout(
			D2D1::Point2F(textRect.left, textTop - verticalOffset_),
			inputTextLayout_.Get(), inputTextBrush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
	}
	if (caretVisible_)
	{
		inputRenderTarget_->FillRectangle(
			D2D1::RectF(caretX, caretTop, caretX + config_.caretWidth, caretTop + caretHeight),
			inputCaretBrush_.Get());
	}
	inputRenderTarget_->PopAxisAlignedClip();
	if (caretOnly)
	{
		inputRenderTarget_->PopAxisAlignedClip();
	}
	const auto endResult = inputRenderTarget_->EndDraw();
	if (endResult == D2DERR_RECREATE_TARGET)
	{
		DiscardInputResources(false);
		return;
	}
	if (FAILED(endResult))
	{
		inputCaretDirtyValid_ = false;
		return;
	}

	inputCaretDirtyRect_ = nextCaretDirty;
	inputCaretDirtyValid_ = nextCaretDirtyValid;
	PresentLayeredSurface(
		inputHwnd_,
		inputSurface_->dc,
		width,
		height,
		caretOnly ? &inputCaretDirtyRect_ : nullptr);
}

float InputBoxHost::GetFeatureWindowWidthDip() const
{
	const auto count = (std::max)(size_t{ 1 }, featurePager_.CurrentItemCount());
	return (std::max)(1.0f,
		static_cast<float>(count) * featureConfig_.cellSize
		+ static_cast<float>(count - 1) * featureConfig_.gap);
}

void InputBoxHost::GetFeatureSurfaceMetrics(int& width, int& height, float& renderScale) const
{
	const auto requestedWidth = (std::max)(
		1,
		DipToPixels(GetFeatureWindowWidthDip(), featureDpi_));
	const auto requestedHeight = (std::max)(
		1,
		DipToPixels(featureConfig_.cellSize, featureDpi_));

	int availableWidth = requestedWidth;
	int availableHeight = requestedHeight;
	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (featureHwnd_ != nullptr
			? MonitorFromWindow(featureHwnd_, MONITOR_DEFAULTTONEAREST)
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
				static_cast<float>(featureConfig_.bottomMargin),
				featureDpi_)),
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
		std::sqrt(static_cast<double>(MaxFeatureSurfacePixels) / requestedPixels),
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
		> MaxFeatureSurfacePixels / static_cast<int64_t>(height))
	{
		width = static_cast<int>(MaxFeatureSurfacePixels / static_cast<int64_t>(height));
	}
	renderScale = static_cast<float>((std::min)(
		static_cast<double>(width) / static_cast<double>(requestedWidth),
		static_cast<double>(height) / static_cast<double>(requestedHeight)));
}

float InputBoxHost::GetInputLineHeightDip() const
{
	return (std::max)(1.0f, config_.fontSize * 1.25f);
}

float InputBoxHost::GetInputWindowHeightDip() const
{
	const auto singleLineHeight = (std::max)(
		static_cast<float>(config_.height),
		2.0f * (std::max)(0.0f, config_.verticalPadding) + GetInputLineHeightDip());
	const auto requestedHeight = singleLineHeight
		+ static_cast<float>((std::max)(1, inputLineCapacity_) - 1) * GetInputLineHeightDip();

	const auto monitor = targetMonitor_ != nullptr
		? targetMonitor_
		: (inputHwnd_ != nullptr
			? MonitorFromWindow(inputHwnd_, MONITOR_DEFAULTTONEAREST)
			: nullptr);
	if (monitor == nullptr) return requestedHeight;
	MONITORINFO monitorInfo{};
	monitorInfo.cbSize = sizeof(monitorInfo);
	if (!GetMonitorInfoW(monitor, &monitorInfo)) return requestedHeight;
	const auto workHeight = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;
	const auto placementMargin = config_.positionMode == 1 || config_.positionMode == 3
		? 0
		: DipToPixels(static_cast<float>(config_.bottomMargin), inputDpi_);
	const auto widthPixels = (std::max)(
		1,
		DipToPixels(static_cast<float>(config_.width), inputDpi_));
	const auto areaLimitedHeight = static_cast<LONG>((std::max)(
		int64_t{ 1 },
		MaxInputSurfacePixels / static_cast<int64_t>(widthPixels)));
	const auto maximumHeightPixels = (std::max)(1L,
		(std::min)({
			4096L,
			areaLimitedHeight,
			static_cast<LONG>(workHeight) - static_cast<LONG>(placementMargin),
		}));
	return (std::min)(requestedHeight, PixelsToDip(maximumHeightPixels, inputDpi_));
}

float InputBoxHost::GetInputTextWidthDip() const
{
	return (std::max)(
		1.0f,
		static_cast<float>(config_.width) - 2.0f * (std::max)(0.0f, config_.horizontalPadding));
}

float InputBoxHost::GetInputTextTopDip() const
{
	return (std::max)(
		(std::max)(0.0f, config_.verticalPadding),
		(GetInputWindowHeightDip()
			- static_cast<float>((std::max)(1, inputLineCapacity_)) * GetInputLineHeightDip()) / 2.0f);
}

float InputBoxHost::GetInputTextViewportHeightDip() const
{
	const auto requestedViewport = static_cast<float>((std::max)(1, inputLineCapacity_))
		* GetInputLineHeightDip();
	const auto availableViewport = (std::max)(
		1.0f,
		GetInputWindowHeightDip() - GetInputTextTopDip()
			- (std::max)(0.0f, config_.verticalPadding));
	return (std::min)(requestedViewport, availableViewport);
}

int InputBoxHost::GetInputWindowPixelWidth() const
{
	return (std::max)(1, DipToPixels(static_cast<float>(config_.width), inputDpi_));
}

int InputBoxHost::GetInputWindowPixelHeight() const
{
	return (std::max)(1, DipToPixels(GetInputWindowHeightDip(), inputDpi_));
}

int InputBoxHost::GetFeatureWindowPixelWidth() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetFeatureSurfaceMetrics(width, height, renderScale);
	return width;
}

int InputBoxHost::GetFeatureWindowPixelHeight() const
{
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetFeatureSurfaceMetrics(width, height, renderScale);
	return height;
}

void InputBoxHost::RenderFeature()
{
	if (featureHwnd_ == nullptr || featureItems_.empty() || FAILED(EnsureFeatureResources())) return;
	int width = 0;
	int height = 0;
	float renderScale = 1.0f;
	GetFeatureSurfaceMetrics(width, height, renderScale);
	RECT bindRect{ 0, 0, width, height };
	if (FAILED(featureRenderTarget_->BindDC(featureSurface_->dc, &bindRect))) return;
	featureRenderTarget_->BeginDraw();
	featureRenderTarget_->SetTransform(D2D1::Matrix3x2F::Scale(renderScale, renderScale));
	featureRenderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
	featureRenderTarget_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
	featureRenderTarget_->Clear(D2D1::ColorF(0, 0.0f));
	const auto count = featurePager_.CurrentItemCount();
	const auto start = featurePager_.FirstItemIndex();
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
		PresentLayeredSurface(featureHwnd_, featureSurface_->dc, width, height);
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
		if (featureConfig_.firstItemVirtualKey >= L'0'
			&& featureConfig_.firstItemVirtualKey <= L'9'
			&& wParam >= VK_NUMPAD0
			&& wParam <= VK_NUMPAD9)
		{
			const auto firstNumpadKey = static_cast<WPARAM>(
				VK_NUMPAD0 + featureConfig_.firstItemVirtualKey - L'0');
			if (wParam >= firstNumpadKey
				&& wParam < firstNumpadKey + static_cast<WPARAM>(featureConfig_.itemsPerPage))
			{
				ActivateFeature(static_cast<size_t>(wParam - firstNumpadKey));
				return true;
			}
		}
	}
	return false;
}

void InputBoxHost::ChangeFeaturePage(int direction)
{
	if (!featurePager_.Move(direction)) return;
	UpdateFeatureWindowGeometry();
	RenderFeature();
}

void InputBoxHost::ActivateFeature(size_t indexOnPage)
{
	size_t absoluteIndex = 0;
	if (!featurePager_.TryResolveIndex(indexOnPage, absoluteIndex)) return;
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
			RenderInput(true);
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
		int width = 0;
		int height = 0;
		float renderScale = 1.0f;
		GetFeatureSurfaceMetrics(width, height, renderScale);
		const auto x = PixelsToDip(GET_X_LPARAM(lParam), featureDpi_)
			/ (std::max)(renderScale, 0.0001f);
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
		if (featureVisible_ && !updatingFeatureWindowGeometry_) RenderFeature();
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
