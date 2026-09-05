#include "rendering/SurfaceShadowWindow.h"

#include "rendering/SurfaceStyleDefaults.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

namespace
{
	constexpr wchar_t ShadowWindowClassName[] = L"SurfaceShadowWindow";
	constexpr int64_t MaxShadowSurfacePixels = 20LL * 1024LL * 1024LL;

	struct ShadowLayer final
	{
		float spread;
		float strokeWidth;
		float opacity;
	};

	constexpr std::array ShadowLayers{
		ShadowLayer{ 6.0f, 2.0f, 0.020f },
		ShadowLayer{ 5.0f, 2.0f, 0.025f },
		ShadowLayer{ 4.0f, 2.0f, 0.030f },
		ShadowLayer{ 3.0f, 2.0f, 0.040f },
		ShadowLayer{ 2.0f, 2.0f, 0.055f },
		ShadowLayer{ 1.75f, 1.5f, 0.075f },
	};

	bool NearlyEqual(float left, float right) noexcept
	{
		return std::abs(left - right) < 0.001f;
	}
}

namespace LuvLetterNative
{
	SurfaceShadowWindow::SurfaceShadowWindow(ID2D1Factory* d2dFactory)
		: d2dFactory_(d2dFactory)
	{
	}

	SurfaceShadowWindow::~SurfaceShadowWindow()
	{
		Detach();
	}

	HRESULT SurfaceShadowWindow::Attach(HWND contentWindow)
	{
		if (contentWindow == nullptr || hwnd_ != nullptr) return E_INVALIDARG;
		WNDCLASSEXW windowClass{};
		windowClass.cbSize = sizeof(windowClass);
		windowClass.lpfnWndProc = &SurfaceShadowWindow::WindowProc;
		windowClass.hInstance = GetModuleHandleW(nullptr);
		windowClass.lpszClassName = ShadowWindowClassName;
		if (RegisterClassExW(&windowClass) == 0
			&& GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
		{
			return LastErrorAsHresult();
		}

		auto window = CreateWindowExW(
			WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED
				| WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
			ShadowWindowClassName,
			L"",
			WS_POPUP,
			0,
			0,
			1,
			1,
			nullptr,
			nullptr,
			GetModuleHandleW(nullptr),
			nullptr);
		if (window == nullptr) return LastErrorAsHresult();
		contentWindow_ = contentWindow;
		hwnd_ = window;
		return S_OK;
	}

	HRESULT SurfaceShadowWindow::Update(
		UINT dpi,
		const std::vector<SurfaceShadowShape>& shapes)
	{
		if (hwnd_ == nullptr || contentWindow_ == nullptr || d2dFactory_ == nullptr)
		{
			return E_POINTER;
		}
		if (shapes.empty())
		{
			Hide();
			return S_OK;
		}
		const auto hasVisibleShape = std::any_of(
			shapes.begin(),
			shapes.end(),
			[](const SurfaceShadowShape& shape)
			{
				return std::isfinite(shape.opacity) && shape.opacity > 0.0f;
			});
		if (!hasVisibleShape)
		{
			Hide();
			return S_OK;
		}

		RECT contentBounds{};
		if (!GetWindowRect(contentWindow_, &contentBounds))
		{
			const auto result = LastErrorAsHresult();
			Hide();
			return result;
		}
		const auto contentWidth = contentBounds.right - contentBounds.left;
		const auto contentHeight = contentBounds.bottom - contentBounds.top;
		if (contentWidth <= 0 || contentHeight <= 0)
		{
			Hide();
			return E_INVALIDARG;
		}
		dpi = NormalizeDpi(dpi);
		const auto inset = (std::max)(1, DipToPixels(SurfaceShadowExtentDip, dpi));
		const auto expandedWidth = static_cast<int64_t>(contentWidth) + 2LL * inset;
		const auto expandedHeight = static_cast<int64_t>(contentHeight) + 2LL * inset;
		if (expandedWidth > (std::numeric_limits<int>::max)()
			|| expandedHeight > (std::numeric_limits<int>::max)())
		{
			Hide();
			return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
		}
		const auto width = static_cast<int>(expandedWidth);
		const auto height = static_cast<int>(expandedHeight);

		if (!SetWindowPos(
			hwnd_,
			contentWindow_,
			contentBounds.left - inset,
			contentBounds.top - inset,
			width,
			height,
			SWP_NOACTIVATE))
		{
			const auto result = LastErrorAsHresult();
			Hide();
			return result;
		}

		const auto needsRender = dpi_ != dpi
			|| contentWidth_ != contentWidth
			|| contentHeight_ != contentHeight
			|| !ShapesEqual(shapes_, shapes);
		if (needsRender)
		{
			const auto result = Render(width, height, inset, dpi, shapes);
			if (FAILED(result))
			{
				Hide();
				return result;
			}
			dpi_ = dpi;
			contentWidth_ = contentWidth;
			contentHeight_ = contentHeight;
			shapes_ = shapes;
		}

		if (!SetWindowPos(
			hwnd_,
			contentWindow_,
			0,
			0,
			0,
			0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
		{
			const auto result = LastErrorAsHresult();
			Hide();
			return result;
		}
		return S_OK;
	}

	void SurfaceShadowWindow::Hide() noexcept
	{
		if (hwnd_ != nullptr) ShowWindow(hwnd_, SW_HIDE);
		DiscardResources(true);
		contentWidth_ = 0;
		contentHeight_ = 0;
		shapes_.clear();
	}

	void SurfaceShadowWindow::Detach() noexcept
	{
		DiscardResources(true);
		if (hwnd_ != nullptr) DestroyWindow(hwnd_);
		hwnd_ = nullptr;
		contentWindow_ = nullptr;
		contentWidth_ = 0;
		contentHeight_ = 0;
		shapes_.clear();
	}

	LRESULT CALLBACK SurfaceShadowWindow::WindowProc(
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
		default:
			return DefWindowProcW(window, message, wParam, lParam);
		}
	}

	bool SurfaceShadowWindow::ShapesEqual(
		const std::vector<SurfaceShadowShape>& left,
		const std::vector<SurfaceShadowShape>& right) noexcept
	{
		if (left.size() != right.size()) return false;
		for (size_t index = 0; index < left.size(); ++index)
		{
			const auto& first = left[index];
			const auto& second = right[index];
			if (!NearlyEqual(first.bounds.left, second.bounds.left)
				|| !NearlyEqual(first.bounds.top, second.bounds.top)
				|| !NearlyEqual(first.bounds.right, second.bounds.right)
				|| !NearlyEqual(first.bounds.bottom, second.bounds.bottom)
				|| !NearlyEqual(first.cornerRadius, second.cornerRadius)
				|| !NearlyEqual(first.opacity, second.opacity))
			{
				return false;
			}
		}
		return true;
	}

	HRESULT SurfaceShadowWindow::Render(
		int width,
		int height,
		int contentInsetPixels,
		UINT dpi,
		const std::vector<SurfaceShadowShape>& shapes)
	{
		auto result = surface_.Ensure(width, height, MaxShadowSurfacePixels);
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
		renderTarget_->SetDpi(static_cast<float>(dpi), static_cast<float>(dpi));
		if (!shadowBrush_)
		{
			result = renderTarget_->CreateSolidColorBrush(
				ColorFromArgb(SurfaceShadowColor),
				shadowBrush_.GetAddressOf());
			if (FAILED(result)) return result;
		}

		RECT bindRect{ 0, 0, width, height };
		result = renderTarget_->BindDC(surface_.DeviceContext(), &bindRect);
		if (FAILED(result))
		{
			return result;
		}
		renderTarget_->BeginDraw();
		renderTarget_->SetTransform(D2D1::Matrix3x2F::Identity());
		renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
		renderTarget_->Clear(D2D1::ColorF(0, 0.0f));
		const auto insetDip = PixelsToDip(contentInsetPixels, dpi);
		for (const auto& shape : shapes)
		{
			const auto opacity = (std::clamp)(shape.opacity, 0.0f, 1.0f);
			if (opacity <= 0.0f) continue;
			for (const auto& layer : ShadowLayers)
			{
				const auto bounds = D2D1::RectF(
					insetDip + shape.bounds.left - layer.spread,
					insetDip + shape.bounds.top - layer.spread + SurfaceShadowOffsetYDip,
					insetDip + shape.bounds.right + layer.spread,
					insetDip + shape.bounds.bottom + layer.spread + SurfaceShadowOffsetYDip);
				const auto rounded = D2D1::RoundedRect(
					bounds,
					(std::max)(0.0f, shape.cornerRadius + layer.spread),
					(std::max)(0.0f, shape.cornerRadius + layer.spread));
				shadowBrush_->SetOpacity(opacity * layer.opacity);
				renderTarget_->DrawRoundedRectangle(
					rounded,
					shadowBrush_.Get(),
					layer.strokeWidth);
			}
		}
		shadowBrush_->SetOpacity(1.0f);
		const auto endResult = renderTarget_->EndDraw();
		if (endResult == D2DERR_RECREATE_TARGET)
		{
			DiscardResources(false);
			return endResult;
		}
		if (FAILED(endResult)) return endResult;
		return surface_.Present(hwnd_, width, height)
			? S_OK
			: LastErrorAsHresult();
	}

	void SurfaceShadowWindow::DiscardResources(bool discardSurface) noexcept
	{
		shadowBrush_.Reset();
		renderTarget_.Reset();
		if (discardSurface) surface_.Reset();
	}
}
