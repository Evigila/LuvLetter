#include "rendering/LayeredWindowSurface.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace LuvLetterNative
{
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

	UINT QueryWindowDpi(HWND window) noexcept
	{
		using GetDpiForWindowFunction = UINT(WINAPI*)(HWND);
		static const auto getDpiForWindow = reinterpret_cast<GetDpiForWindowFunction>(
			GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
		if (window != nullptr && getDpiForWindow != nullptr)
		{
			const auto dpi = getDpiForWindow(window);
			if (dpi != 0) return NormalizeDpi(dpi);
		}

		const auto screenDc = GetDC(nullptr);
		if (screenDc == nullptr) return DefaultDpi;
		const auto dpi = static_cast<UINT>(GetDeviceCaps(screenDc, LOGPIXELSX));
		ReleaseDC(nullptr, screenDc);
		return NormalizeDpi(dpi);
	}

	HRESULT LastErrorAsHresult() noexcept
	{
		const auto error = GetLastError();
		return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
	}

	D2D1_COLOR_F ColorFromArgb(uint32_t argb) noexcept
	{
		return D2D1::ColorF(
			static_cast<float>((argb >> 16) & 0xFF) / 255.0f,
			static_cast<float>((argb >> 8) & 0xFF) / 255.0f,
			static_cast<float>(argb & 0xFF) / 255.0f,
			static_cast<float>((argb >> 24) & 0xFF) / 255.0f);
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
		const auto requestedInset = 0.5f + (std::max)(0.0f, borderThickness) / 2.0f;
		const auto maximumInset = (std::max)(
			0.0f,
			(std::min)(width, height) / 2.0f - 0.01f);
		const auto inset = (std::min)(requestedInset, maximumInset);
		const auto rect = D2D1::RectF(left + inset, top + inset, right - inset, bottom - inset);
		const auto radius = (std::clamp)(
			cornerRadius,
			0.0f,
			(std::max)(
				0.0f,
				(std::min)(rect.right - rect.left, rect.bottom - rect.top) / 2.0f));
		return D2D1::RoundedRect(rect, radius, radius);
	}

	bool IsKeyDown(int virtualKey) noexcept
	{
		return (GetKeyState(virtualKey) & 0x8000) != 0;
	}

	int GetCurrentHotkeyModifiers() noexcept
	{
		int modifiers = 0;
		if (IsKeyDown(VK_MENU)) modifiers |= 1;
		if (IsKeyDown(VK_CONTROL)) modifiers |= 2;
		if (IsKeyDown(VK_SHIFT)) modifiers |= 4;
		if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) modifiers |= 8;
		return modifiers;
	}

	bool MatchesHotkey(WPARAM key, int virtualKey, int modifiers) noexcept
	{
		return virtualKey > 0
			&& key == static_cast<WPARAM>(virtualKey)
			&& GetCurrentHotkeyModifiers() == modifiers;
	}

	LayeredWindowSurface::~LayeredWindowSurface()
	{
		Reset();
	}

	HRESULT LayeredWindowSurface::Ensure(
		int requestedWidth,
		int requestedHeight,
		int64_t maximumPixels)
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
		if (dc_ != nullptr && bitmap_ != nullptr
			&& width_ == requestedWidth && height_ == requestedHeight)
		{
			return S_OK;
		}

		Reset();
		const auto screenDc = GetDC(nullptr);
		if (screenDc == nullptr) return LastErrorAsHresult();

		dc_ = CreateCompatibleDC(screenDc);
		BITMAPINFO bitmapInfo{};
		bitmapInfo.bmiHeader.biSize = sizeof(bitmapInfo.bmiHeader);
		bitmapInfo.bmiHeader.biWidth = requestedWidth;
		bitmapInfo.bmiHeader.biHeight = -requestedHeight;
		bitmapInfo.bmiHeader.biPlanes = 1;
		bitmapInfo.bmiHeader.biBitCount = 32;
		bitmapInfo.bmiHeader.biCompression = BI_RGB;
		bitmapInfo.bmiHeader.biSizeImage = static_cast<DWORD>(pixelCount * 4);
		bitmap_ = CreateDIBSection(
			screenDc,
			&bitmapInfo,
			DIB_RGB_COLORS,
			&bits_,
			nullptr,
			0);
		ReleaseDC(nullptr, screenDc);

		if (dc_ == nullptr || bitmap_ == nullptr)
		{
			const auto hr = LastErrorAsHresult();
			Reset();
			return hr;
		}

		originalBitmap_ = SelectObject(dc_, bitmap_);
		if (originalBitmap_ == nullptr || originalBitmap_ == HGDI_ERROR)
		{
			const auto hr = LastErrorAsHresult();
			originalBitmap_ = nullptr;
			Reset();
			return hr;
		}

		width_ = requestedWidth;
		height_ = requestedHeight;
		return S_OK;
	}

	void LayeredWindowSurface::Clear(const RECT& requestedRect) noexcept
	{
		if (bits_ == nullptr || width_ <= 0 || height_ <= 0) return;
		const auto left = (std::clamp)(requestedRect.left, 0L, static_cast<LONG>(width_));
		const auto top = (std::clamp)(requestedRect.top, 0L, static_cast<LONG>(height_));
		const auto right = (std::clamp)(requestedRect.right, left, static_cast<LONG>(width_));
		const auto bottom = (std::clamp)(requestedRect.bottom, top, static_cast<LONG>(height_));
		const auto stride = static_cast<size_t>(width_) * 4U;
		const auto rowBytes = static_cast<size_t>(right - left) * 4U;
		auto* bytes = static_cast<unsigned char*>(bits_);
		for (auto row = top; row < bottom; ++row)
		{
			std::memset(
				bytes + static_cast<size_t>(row) * stride + static_cast<size_t>(left) * 4U,
				0,
				rowBytes);
		}
	}

	void LayeredWindowSurface::Reset() noexcept
	{
		if (dc_ != nullptr && originalBitmap_ != nullptr) SelectObject(dc_, originalBitmap_);
		if (bitmap_ != nullptr) DeleteObject(bitmap_);
		if (dc_ != nullptr) DeleteDC(dc_);
		dc_ = nullptr;
		bitmap_ = nullptr;
		originalBitmap_ = nullptr;
		bits_ = nullptr;
		width_ = 0;
		height_ = 0;
	}

	bool LayeredWindowSurface::Present(
		HWND window,
		int width,
		int height,
		const RECT* dirtyRect,
		BYTE opacity) const noexcept
	{
		POINT source{ 0, 0 };
		SIZE size{ width, height };
		BLENDFUNCTION blend{ AC_SRC_OVER, 0, opacity, AC_SRC_ALPHA };
		if (dirtyRect != nullptr)
		{
			UPDATELAYEREDWINDOWINFO update{};
			update.cbSize = sizeof(update);
			update.psize = &size;
			update.hdcSrc = dc_;
			update.pptSrc = &source;
			update.pblend = &blend;
			update.dwFlags = ULW_ALPHA;
			update.prcDirty = dirtyRect;
			if (UpdateLayeredWindowIndirect(window, &update)) return true;
		}

		return UpdateLayeredWindow(
			window,
			nullptr,
			nullptr,
			&size,
			dc_,
			&source,
			0,
			&blend,
			ULW_ALPHA) != FALSE;
	}
}
