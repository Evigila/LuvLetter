#pragma once

#include "api/InputBoxApi.h"
#include "rendering/InputBoxAnimator.h"
#include "rendering/LayeredWindowSurface.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <functional>
#include <memory>
#include <string>
#include <vector>

struct QuickActionItem final
{
	uint64_t token = 0;
	std::wstring label;
};

class QuickActionsWindow final
{
public:
	QuickActionsWindow(
		ID2D1Factory* d2dFactory,
		IDWriteFactory* dwriteFactory,
		std::function<void(uint64_t)> activated);
	~QuickActionsWindow() = default;
	QuickActionsWindow(const QuickActionsWindow&) = delete;
	QuickActionsWindow& operator=(const QuickActionsWindow&) = delete;

	HRESULT Attach(HWND window);
	void SetPeerWindow(HWND peerWindow) noexcept { peerHwnd_ = peerWindow; }
	void ApplyConfiguration(const LuvLetterFeatureWindowConfig& config);
	void SetItems(std::vector<QuickActionItem>&& items);
	void Show(HMONITOR targetMonitor, HWND previousForegroundWindow);
	void Hide();
	bool IsVisible() const noexcept { return visible_; }
	bool IsEmpty() const noexcept { return items_.empty(); }
	HWND WindowHandle() const noexcept { return hwnd_; }
	int PixelWidth() const;
	int PixelHeight() const;
	LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);

private:
	HRESULT EnsureResources();
	void DiscardResources(bool discardSurface);
	void UpdateWindowShape() const;
	void RefreshDpiFromWindow();
	void ApplyDpiChange(UINT dpi, const RECT* suggestedRect);
	void UpdateGeometry();
	void ReleaseFocus();
	void SynchronizeAnimation();
	void AdvanceAnimation();
	void CompleteHide();
	void UpdatePosition(bool applyAnimation = true) const;
	float WindowWidthDip() const;
	void GetSurfaceMetrics(int& width, int& height, float& renderScale) const;
	void Render();
	bool HandleKeyDown(WPARAM key);
	void ChangePage(int direction);
	void Activate(size_t indexOnPage);

	void ResetPaging(size_t itemsPerPage) noexcept;
	void SetItemsPerPage(size_t itemsPerPage) noexcept;
	bool MovePage(int direction) noexcept;
	size_t PageCount() const noexcept;
	size_t CurrentItemCount() const noexcept;
	size_t FirstItemIndex() const noexcept;
	bool TryResolveIndex(size_t indexOnPage, size_t& absoluteIndex) const noexcept;
	void ClampCurrentPage() noexcept;

	HWND hwnd_ = nullptr;
	HWND peerHwnd_ = nullptr;
	HWND previousForegroundHwnd_ = nullptr;
	HMONITOR targetMonitor_ = nullptr;
	UINT dpi_ = LuvLetterNative::DefaultDpi;
	bool visible_ = false;
	bool updatingGeometry_ = false;
	ULONGLONG animationTimestamp_ = 0;
	PopupAnimator animator_{ PopupAnimationSettings{ 180.0, 140.0, 1.0f, -72.0f } };

	LuvLetterFeatureWindowConfig config_{};
	std::vector<QuickActionItem> items_;
	size_t itemsPerPage_ = 1;
	size_t currentPage_ = 0;
	std::function<void(uint64_t)> activated_;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> numberFormat_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> backgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> accentBrush_;
	std::unique_ptr<LuvLetterNative::LayeredWindowSurface> surface_;
};
