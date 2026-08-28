#pragma once

#include "api/InputBoxApi.h"
#include "rendering/LayeredWindowSurface.h"
#include "windows/InputCandidateState.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <functional>
#include <memory>
#include <vector>

class InputCandidatesWindow final
{
public:
	InputCandidatesWindow(
		ID2D1Factory* d2dFactory,
		IDWriteFactory* dwriteFactory,
		std::function<void(uint64_t, int32_t)> activated);
	~InputCandidatesWindow() = default;
	InputCandidatesWindow(const InputCandidatesWindow&) = delete;
	InputCandidatesWindow& operator=(const InputCandidatesWindow&) = delete;

	HRESULT Attach(HWND window, HWND inputWindow);
	void ApplyConfiguration(const LuvLetterInputBoxConfig& config);
	bool SetItems(
		std::vector<InputCandidateItem>&& items,
		uint64_t revision,
		uint64_t currentInputRevision,
		bool inputVisible);
	void Clear();
	bool MoveSelection(int direction);
	bool ActivateSelected(LuvLetterCandidateAction action);
	void Hide();
	void SynchronizeToInputWindow();
	bool IsVisible() const noexcept { return visible_; }
	bool IsEmpty() const noexcept { return state_.IsEmpty(); }
	HWND WindowHandle() const noexcept { return hwnd_; }
	int PixelWidth() const;
	int PixelHeight() const;
	LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);

private:
	HRESULT EnsureResources();
	void DiscardResources(bool discardSurface);
	void Show();
	void UpdateGeometry();
	void UpdatePosition() const;
	float WindowHeightDip() const noexcept;
	float RenderScaleY() const noexcept;
	void Render();
	const wchar_t* KindLabel(LuvLetterCandidateKind kind) const noexcept;

	HWND hwnd_ = nullptr;
	HWND inputHwnd_ = nullptr;
	UINT dpi_ = LuvLetterNative::DefaultDpi;
	bool visible_ = false;
	bool updatingGeometry_ = false;
	LuvLetterInputBoxConfig config_{};
	InputCandidateState state_;
	std::function<void(uint64_t, int32_t)> activated_;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> primaryTextFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> secondaryTextFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> kindTextFormat_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> backgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> secondaryTextBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> selectionBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> separatorBrush_;
	std::unique_ptr<LuvLetterNative::LayeredWindowSurface> surface_;
};
