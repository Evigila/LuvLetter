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

class InputWindow final
{
public:
	InputWindow(
		ID2D1Factory* d2dFactory,
		IDWriteFactory* dwriteFactory,
		std::function<void(const std::wstring&)> submitted);
	~InputWindow() = default;
	InputWindow(const InputWindow&) = delete;
	InputWindow& operator=(const InputWindow&) = delete;

	HRESULT Attach(HWND window);
	void SetPeerWindow(HWND peerWindow) noexcept { peerHwnd_ = peerWindow; }
	void ApplyConfiguration(const LuvLetterInputBoxConfig& config);
	void Show(HMONITOR targetMonitor, HWND previousForegroundWindow);
	void Hide();
	void HideImmediately();
	bool IsVisible() const noexcept { return visible_; }
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
	void ReleaseFocus();
	void SynchronizeAnimation();
	void AdvanceAnimation();
	void CompleteHide();
	void UpdateWindowPosition(bool applyAnimation = true) const;
	void SetFocusIndicatorTarget(bool focused) noexcept;

	void Reset();
	void Submit();
	void InsertText(const std::wstring& value);
	void InsertCharacter(wchar_t value);
	void DeleteBeforeCaret(bool byWord = false);
	void DeleteAtCaret(bool byWord = false);
	void MoveCaretLeft(bool extendSelection = false, bool byWord = false);
	void MoveCaretRight(bool extendSelection = false, bool byWord = false);
	void MoveCaretToStart(bool extendSelection = false);
	void MoveCaretToEnd(bool extendSelection = false);
	void MoveCaretTo(size_t index, bool extendSelection);
	void SelectAll();
	bool HasSelection() const noexcept;
	size_t SelectionStart() const noexcept;
	size_t SelectionEnd() const noexcept;
	void CollapseSelection() noexcept;
	void EraseRange(size_t start, size_t end);
	bool EraseSelection();
	size_t PreviousWordBoundary(size_t index) const noexcept;
	size_t NextWordBoundary(size_t index) const noexcept;
	void NavigateHistory(int direction);
	bool CopySelectionToClipboard() const;
	void CutSelectionToClipboard();
	void PasteFromClipboard();
	void SetCaretFromPoint(LPARAM point, bool extendSelection = false);
	void Invalidate();
	void UpdateResponsiveHeight();
	void UpdateImeCompositionWindow();
	bool HasKeyboardFocus() const noexcept;
	bool RefreshCaretState(bool restartBlink, bool forceInactive = false) noexcept;
	void EnsureCaretVisible();
	D2D1_POINT_2F GetCaretLogicalPosition();
	float LineHeightDip() const;
	float WindowHeightDip() const;
	float TextWidthDip() const;
	float TextTopDip() const;
	float TextViewportHeightDip() const;
	float FocusIndicatorProgress() const noexcept;
	float FocusIndicatorReservationDip() const noexcept;
	void Render(bool caretOnly = false);
	bool HandleKeyDown(WPARAM key);

	void RecordHistory(const std::wstring& value);
	void ResetHistoryNavigation() noexcept;
	bool TryNavigateHistory(int direction, std::wstring& value);

	HWND hwnd_ = nullptr;
	HWND peerHwnd_ = nullptr;
	HWND previousForegroundHwnd_ = nullptr;
	HMONITOR targetMonitor_ = nullptr;
	UINT dpi_ = LuvLetterNative::DefaultDpi;
	bool visible_ = false;
	bool caretVisible_ = false;
	ULONGLONG animationTimestamp_ = 0;
	bool caretDirtyValid_ = false;
	RECT caretDirtyRect_{};

	std::wstring text_;
	size_t caretIndex_ = 0;
	size_t selectionAnchor_ = 0;
	bool mouseSelecting_ = false;
	int lineCapacity_ = 1;
	float verticalOffset_ = 0.0f;
	PopupAnimator animator_;
	PopupAnimator focusIndicatorAnimator_{ PopupAnimationSettings{ 140.0, 120.0, 1.0f, 0.0f } };
	std::vector<std::wstring> historyEntries_;
	std::wstring historyDraft_;
	int historyNavigationIndex_ = -1;

	LuvLetterInputBoxConfig config_{};
	std::function<void(const std::wstring&)> submitted_;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> backgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> placeholderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> caretBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> focusIndicatorBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> selectionBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> selectionTextBrush_;
	std::unique_ptr<LuvLetterNative::LayeredWindowSurface> surface_;
};
