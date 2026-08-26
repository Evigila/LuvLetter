#pragma once

#include <Windows.h>
#include <d2d1.h>

#include <cstdint>

namespace LuvLetterNative
{
	inline constexpr UINT DefaultDpi = 96;

	UINT NormalizeDpi(UINT dpi) noexcept;
	int DipToPixels(float value, UINT dpi) noexcept;
	float PixelsToDip(int value, UINT dpi) noexcept;
	UINT QueryWindowDpi(HWND window) noexcept;
	HRESULT LastErrorAsHresult() noexcept;
	D2D1_COLOR_F ColorFromArgb(uint32_t argb) noexcept;
	D2D1_ROUNDED_RECT CreateInsetRoundedRect(
		float left,
		float top,
		float right,
		float bottom,
		float cornerRadius,
		float borderThickness) noexcept;
	bool IsKeyDown(int virtualKey) noexcept;
	int GetCurrentHotkeyModifiers() noexcept;
	bool MatchesHotkey(WPARAM key, int virtualKey, int modifiers) noexcept;

	class LayeredWindowSurface final
	{
	public:
		LayeredWindowSurface() = default;
		~LayeredWindowSurface();
		LayeredWindowSurface(const LayeredWindowSurface&) = delete;
		LayeredWindowSurface& operator=(const LayeredWindowSurface&) = delete;

		HRESULT Ensure(int width, int height, int64_t maximumPixels);
		void Clear(const RECT& rect) noexcept;
		void Reset() noexcept;
		bool Present(
			HWND window,
			int width,
			int height,
			const RECT* dirtyRect = nullptr,
			BYTE opacity = 255) const noexcept;

		HDC DeviceContext() const noexcept { return dc_; }

	private:
		HDC dc_ = nullptr;
		HBITMAP bitmap_ = nullptr;
		HGDIOBJ originalBitmap_ = nullptr;
		void* bits_ = nullptr;
		int width_ = 0;
		int height_ = 0;
	};
}
