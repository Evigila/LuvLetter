#pragma once

#include "rendering/LayeredWindowSurface.h"

#include <Windows.h>
#include <d2d1.h>
#include <wrl/client.h>

#include <vector>

namespace LuvLetterNative
{
	struct SurfaceShadowShape final
	{
		D2D1_RECT_F bounds{};
		float cornerRadius = 0.0f;
		float opacity = 1.0f;
	};

	class SurfaceShadowWindow final
	{
	public:
		explicit SurfaceShadowWindow(ID2D1Factory* d2dFactory);
		~SurfaceShadowWindow();
		SurfaceShadowWindow(const SurfaceShadowWindow&) = delete;
		SurfaceShadowWindow& operator=(const SurfaceShadowWindow&) = delete;

		HRESULT Attach(HWND contentWindow);
		HRESULT Update(UINT dpi, const std::vector<SurfaceShadowShape>& shapes);
		void Hide() noexcept;
		void Detach() noexcept;

	private:
		static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
		static bool ShapesEqual(
			const std::vector<SurfaceShadowShape>& left,
			const std::vector<SurfaceShadowShape>& right) noexcept;
		HRESULT Render(
			int width,
			int height,
			int contentInsetPixels,
			UINT dpi,
			const std::vector<SurfaceShadowShape>& shapes);
		void DiscardResources(bool discardSurface) noexcept;

		HWND contentWindow_ = nullptr;
		HWND hwnd_ = nullptr;
		UINT dpi_ = DefaultDpi;
		int contentWidth_ = 0;
		int contentHeight_ = 0;
		std::vector<SurfaceShadowShape> shapes_;
		Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
		Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
		Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> shadowBrush_;
		LayeredWindowSurface surface_;
	};
}
