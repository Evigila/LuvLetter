#pragma once

#include "api/InputBoxApi.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <memory>
#include <mutex>
#include <string>
#include <vector>

class InputBoxHost
{
public:
	static InputBoxHost& Instance();

	HRESULT ApplyConfig(const LuvLetterInputBoxConfig& config);
	HRESULT SetInputSubmittedCallback(LuvLetterInputSubmittedCallback callback, void* context);
	HRESULT Show();
	HRESULT Hide();
	HRESULT Toggle();

	HRESULT ApplyFeatureConfig(const LuvLetterFeatureWindowConfig& config);
	HRESULT SetFeatureItems(const LuvLetterFeatureItem* items, int32_t count);
	HRESULT SetFeatureActivatedCallback(LuvLetterFeatureActivatedCallback callback, void* context);
	HRESULT ShowFeatureWindow();
	HRESULT HideFeatureWindow();
	HRESULT ToggleFeatureWindow();

	HRESULT Shutdown();

private:
	enum class WindowKind : uint8_t
	{
		Input,
		Feature,
	};

	struct WindowContext
	{
		InputBoxHost* host;
		WindowKind kind;
	};

	struct HostRequest;
	struct CachedSurface;
	struct FeatureItem;

	InputBoxHost();
	~InputBoxHost();
	InputBoxHost(const InputBoxHost&) = delete;
	InputBoxHost& operator=(const InputBoxHost&) = delete;

	HRESULT EnsureThread();
	HRESULT EnsureThreadLocked();
	HRESULT DispatchRequest(HostRequest* request, bool waitForCompletion);
	HRESULT ProcessRequest(HostRequest& request);
	void CompleteRequest(HostRequest* request, HRESULT result) noexcept;
	HRESULT Run();
	HRESULT CreateWindows();
	HRESULT CreateWindowForKind(WindowKind kind);

	HRESULT EnsureFactories();
	HRESULT EnsureInputResources();
	HRESULT EnsureFeatureResources();
	void DiscardInputResources(bool discardSurface);
	void DiscardFeatureResources(bool discardSurface);
	void DiscardAllResources();

	void ApplyConfigOnUiThread(const LuvLetterInputBoxConfig& config);
	void ApplyFeatureConfigOnUiThread(const LuvLetterFeatureWindowConfig& config);
	void SetFeatureItemsOnUiThread(std::vector<FeatureItem>&& items);
	void UpdateInputWindowShape() const;
	void UpdateFeatureWindowShape() const;
	void ShowInputWindowAndFocus();
	void HideInputWindow();
	void ShowFeatureWindowAndFocus();
	void HideFeatureWindowOnUiThread();
	void UpdateInputWindowPosition() const;
	void UpdateFeatureWindowGeometry();
	void UpdateFeatureWindowPosition() const;
	void RefreshInputDpiFromWindow();
	void RefreshFeatureDpiFromWindow();
	void ApplyInputDpiChange(UINT dpi, const RECT* suggestedRect);
	void ApplyFeatureDpiChange(UINT dpi, const RECT* suggestedRect);
	HMONITOR CaptureTargetMonitor() const;

	void RenderInput();
	void RenderFeature();
	void ResetInput();
	void SubmitInput();
	void InsertText(const std::wstring& value);
	void InsertCharacter(wchar_t value);
	void DeleteBeforeCaret();
	void DeleteAtCaret();
	void MoveCaretLeft();
	void MoveCaretRight();
	void MoveCaretToStart();
	void MoveCaretToEnd();
	void NavigateHistory(int direction);
	void PasteFromClipboard();
	void SetCaretFromPoint(LPARAM lParam);
	void InvalidateInput();
	void UpdateImeCompositionWindow();
	void EnsureCaretVisible();
	float GetCaretLogicalX();
	bool HandleInputKeyDown(WPARAM wParam);
	bool HandleFeatureKeyDown(WPARAM wParam);
	void ChangeFeaturePage(int direction);
	void ActivateFeature(size_t indexOnPage);
	size_t GetFeaturePageCount() const;
	size_t GetFeaturePageItemCount() const;
	float GetFeatureWindowWidthDip() const;
	int GetInputWindowPixelWidth() const;
	int GetInputWindowPixelHeight() const;
	int GetFeatureWindowPixelWidth() const;
	int GetFeatureWindowPixelHeight() const;

	LRESULT HandleInputMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
	LRESULT HandleFeatureMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
	LRESULT DispatchWindowMessage(WindowKind kind, HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	static LuvLetterInputBoxConfig SanitizeConfig(const LuvLetterInputBoxConfig& config);
	static LuvLetterFeatureWindowConfig SanitizeFeatureConfig(const LuvLetterFeatureWindowConfig& config);
	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT DispatchWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept;
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept;

	// Lifecycle coordination only. UI state below this point belongs exclusively to threadId_.
	std::mutex lifecycleMutex_;
	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = E_PENDING;
	bool stopping_ = false;

	WindowContext inputWindowContext_{ this, WindowKind::Input };
	WindowContext featureWindowContext_{ this, WindowKind::Feature };
	HWND inputHwnd_ = nullptr;
	HWND featureHwnd_ = nullptr;
	bool inputVisible_ = false;
	bool featureVisible_ = false;
	bool caretVisible_ = true;
	HMONITOR targetMonitor_ = nullptr;
	UINT inputDpi_ = 96;
	UINT featureDpi_ = 96;

	std::wstring text_;
	size_t caretIndex_ = 0;
	float horizontalOffset_ = 0.0f;
	std::vector<std::wstring> history_;
	int historyIndex_ = -1;
	std::wstring historyDraft_;
	LuvLetterInputBoxConfig config_{};
	LuvLetterInputSubmittedCallback inputSubmittedCallback_ = nullptr;
	void* inputSubmittedContext_ = nullptr;

	LuvLetterFeatureWindowConfig featureConfig_{};
	std::vector<FeatureItem> featureItems_;
	size_t featurePage_ = 0;
	LuvLetterFeatureActivatedCallback featureActivatedCallback_ = nullptr;
	void* featureActivatedContext_ = nullptr;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;

	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> inputRenderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> inputTextFormat_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> inputBackgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> inputBorderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> inputTextBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> inputPlaceholderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> inputCaretBrush_;
	std::unique_ptr<CachedSurface> inputSurface_;

	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> featureRenderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> featureTextFormat_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> featureNumberFormat_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> featureBackgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> featureBorderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> featureTextBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> featureAccentBrush_;
	std::unique_ptr<CachedSurface> featureSurface_;
};
