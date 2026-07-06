#pragma once

#include "api/InputBoxApi.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <string>
#include <vector>

class InputBoxHost
{
public:
	static InputBoxHost& Instance();

	HRESULT ApplyConfig(const LuvLetterInputBoxConfig& config);
	HRESULT Show();
	HRESULT Hide();
	HRESULT Toggle();
	void Shutdown();

private:
	InputBoxHost();
	~InputBoxHost() = default;
	InputBoxHost(const InputBoxHost&) = delete;
	InputBoxHost& operator=(const InputBoxHost&) = delete;

	HRESULT EnsureThread();
	HRESULT Run();
	HRESULT CreateInputWindow();
	HRESULT EnsureResources();
	void DiscardResources();
	void UpdateWindowShape() const;
	HRGN CreateWindowRegion() const;
	void ShowWindowAndFocus();
	void HideWindow();
	void Render();
	void Resize(UINT width, UINT height);
	void UpdateWindowPosition() const;
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
	float GetCaretX(const D2D1_RECT_F& textRect);
	LRESULT HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
	static LuvLetterInputBoxConfig SanitizeConfig(const LuvLetterInputBoxConfig& config);

	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = E_FAIL;
	HWND hwnd_ = nullptr;
	bool visible_ = false;
	bool caretVisible_ = true;
	std::wstring text_;
	size_t caretIndex_ = 0;
	std::vector<std::wstring> history_;
	int historyIndex_ = -1;
	std::wstring historyDraft_;
	LuvLetterInputBoxConfig config_{};

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> placeholderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> caretBrush_;
};
