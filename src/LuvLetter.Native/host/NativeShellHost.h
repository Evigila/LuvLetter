#pragma once

#include "api/InputBoxApi.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <memory>
#include <mutex>
#include <string>

class InputWindow;
class InputCandidatesWindow;
class MessageQueueWindow;
class QuickActionsWindow;

class NativeShellHost final
{
public:
	static NativeShellHost& Instance();

	HRESULT ApplyConfig(const LuvLetterInputBoxConfig& config);
	HRESULT SetInputSubmittedCallback(LuvLetterInputSubmittedCallback callback, void* context);
	HRESULT SetInputChangedCallback(LuvLetterInputChangedCallback callback, void* context);
	HRESULT SetCandidateActivatedCallback(
		LuvLetterCandidateActivatedCallback callback,
		void* context);
	HRESULT SetInputCandidates(
		const LuvLetterInputCandidate* items,
		int32_t count,
		uint64_t revision);
	HRESULT Show();
	HRESULT Hide();
	HRESULT Toggle();
	HRESULT ApplyQuickActionsConfig(const LuvLetterFeatureWindowConfig& config);
	HRESULT SetQuickActions(const LuvLetterFeatureItem* items, int32_t count);
	HRESULT SetQuickActionActivatedCallback(
		LuvLetterFeatureActivatedCallback callback,
		void* context);
	HRESULT ShowQuickActionsWindow();
	HRESULT HideQuickActionsWindow();
	HRESULT ToggleQuickActionsWindow();
	HRESULT EnqueueMessage(const wchar_t* text, int32_t length);
	HRESULT ToggleMessageQueue();
	HRESULT HideMessageQueue();
	HRESULT HidePopups();
	HRESULT Shutdown();

private:
	enum class WindowKind : uint8_t
	{
		Input,
		InputCandidates,
		QuickActions,
		MessageQueue,
	};

	struct WindowContext
	{
		NativeShellHost* host;
		WindowKind kind;
	};

	struct HostRequest;

	NativeShellHost() = default;
	~NativeShellHost();
	NativeShellHost(const NativeShellHost&) = delete;
	NativeShellHost& operator=(const NativeShellHost&) = delete;

	HRESULT EnsureThread();
	HRESULT EnsureThreadLocked();
	HRESULT DispatchRequest(HostRequest* request, bool waitForCompletion);
	HRESULT ProcessRequest(HostRequest& request);
	void CompleteRequest(HostRequest* request, HRESULT result) noexcept;
	HRESULT Run();
	HRESULT EnsureFactories();
	HRESULT CreateWindows();
	HRESULT CreateWindowForKind(WindowKind kind);
	void DestroyWindows() noexcept;
	HMONITOR CaptureTargetMonitor() const;
	void CapturePreviousForegroundWindow() noexcept;
	bool TryActivateInteractiveWindow(HWND target, HWND previousForeground) noexcept;
	void OnInputSubmitted(const std::wstring& text, int32_t inputMode) noexcept;
	void OnInputChanged(
		const std::wstring& text,
		int32_t inputMode,
		uint64_t revision) noexcept;
	void OnCandidateActivated(uint64_t token, int32_t action) noexcept;
	void OnQuickActionActivated(uint64_t token) noexcept;
	LRESULT DispatchWindowMessage(
		WindowKind kind,
		HWND window,
		UINT message,
		WPARAM wParam,
		LPARAM lParam);

	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT DispatchWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept;
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept;

	std::mutex lifecycleMutex_;
	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = E_PENDING;
	bool stopping_ = false;

	WindowContext inputWindowContext_{ this, WindowKind::Input };
	WindowContext inputCandidatesWindowContext_{ this, WindowKind::InputCandidates };
	WindowContext quickActionsWindowContext_{ this, WindowKind::QuickActions };
	WindowContext messageQueueWindowContext_{ this, WindowKind::MessageQueue };
	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	std::unique_ptr<InputWindow> inputWindow_;
	std::unique_ptr<InputCandidatesWindow> inputCandidatesWindow_;
	std::unique_ptr<QuickActionsWindow> quickActionsWindow_;
	std::unique_ptr<MessageQueueWindow> messageQueueWindow_;
	HWND previousForegroundHwnd_ = nullptr;
	LuvLetterInputSubmittedCallback inputSubmittedCallback_ = nullptr;
	void* inputSubmittedContext_ = nullptr;
	LuvLetterInputChangedCallback inputChangedCallback_ = nullptr;
	void* inputChangedContext_ = nullptr;
	LuvLetterCandidateActivatedCallback candidateActivatedCallback_ = nullptr;
	void* candidateActivatedContext_ = nullptr;
	LuvLetterFeatureActivatedCallback quickActionActivatedCallback_ = nullptr;
	void* quickActionActivatedContext_ = nullptr;
};
