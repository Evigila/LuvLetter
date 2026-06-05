#pragma once

#include "api/OverlayApi.h"
#include "interop/OverlayEventBridge.h"
#include "interop/OverlayRequestDispatcher.h"
#include "layout/LayoutEngine.h"
#include "layout/LayoutSnapshot.h"
#include "render/AnimationSystem.h"
#include "render/OverlayRenderer.h"
#include "state/OverlayStateStore.h"

#include <Windows.h>

#include <atomic>
#include <cstddef>
#include <deque>
#include <initializer_list>
#include <optional>

class OverlayHost
{
public:
	static OverlayHost& Instance();

	HRESULT Start(const LuvLetterOverlayStartOptions& options);
	void Stop();

	HRESULT UpdateLayout(const LuvLetterOverlayLayoutConfig& layoutConfig);
	HRESULT UpdateLogo(const uint8_t* logoData, size_t logoSize);
	HRESULT UpdateText(const wchar_t* text, int32_t textLength);
	HRESULT UpdateInputText(const wchar_t* text, int32_t textLength);
	HRESULT UpdateOutputText(const wchar_t* text, int32_t textLength);
	HRESULT UpdateOutputNavigation(bool canPageUp, bool canPageDown);
	HRESULT SetVisualMode(LuvLetterOverlayVisualMode visualMode);
	void SetEventCallback(LuvLetterOverlayEventCallback callback, void* context);

private:
	OverlayHost() = default;
	~OverlayHost() = default;
	OverlayHost(const OverlayHost&) = delete;
	OverlayHost& operator=(const OverlayHost&) = delete;

	HRESULT Run();
	HRESULT CreateOverlayWindow();
	bool TryGetAnchorMonitorInfo(MONITORINFO& monitorInfo) const;
	void RefreshLayout(bool playEntranceAnimation);
	void TransitionToVisualMode(LuvLetterOverlayVisualMode visualMode);
	void StartBehaviorMonitoring();
	void StopBehaviorMonitoring();
	void RestartBadgeInactivityTimer();
	void CancelBadgeInactivityTimer();
	void StartInputCursorBlink();
	void StopInputCursorBlink();
	void SetBadgeActiveState(bool isActive, bool restartTimer);
	void UpdateWindowOpacity() const;
	void EvaluateBadgeBehavior();
	bool IsCursorInsideBadgeCourtesyZone() const;
	BYTE ComputeBadgeInactiveAlpha() const;
	void ApplyWindowRect(const RECT& windowRect) const;
	void AdvanceAnimation();
	void HandleQueuedRequests();
	void UpdateLayoutSnapshotForRect(const RECT& windowRect);
	void StartAnimationSequence(
		const RECT& fromRect,
		std::initializer_list<RECT> targetRects,
		std::optional<LuvLetterOverlayVisualMode> finalVisualMode);
	void BeginNextAnimationPhase(const RECT& fromRect);
	RECT GetCurrentWindowRect() const;
	LRESULT HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	static DWORD WINAPI ThreadEntry(LPVOID parameter);
	static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

	HANDLE threadHandle_ = nullptr;
	DWORD threadId_ = 0;
	HANDLE startedEvent_ = nullptr;
	HRESULT startResult_ = S_OK;
	std::atomic<bool> running_ = false;

	HWND hwnd_ = nullptr;
	OverlayStateStore stateStore_{};
	LayoutEngine layoutEngine_{};
	OverlayRenderer renderer_{};
	AnimationSystem animationSystem_{};
	OverlayEventBridge eventBridge_{};
	OverlayRequestDispatcher requestDispatcher_{};
	OverlayLayoutSnapshot layoutSnapshot_{};
	mutable RECT lastAppliedWindowRect_{};
	std::deque<RECT> animationTargets_{};
	std::optional<LuvLetterOverlayVisualMode> finalVisualModeAfterAnimation_{};
	bool badgeCourtesyHidden_ = false;
};
