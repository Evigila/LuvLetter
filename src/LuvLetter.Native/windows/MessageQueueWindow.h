#pragma once

#include "rendering/LayeredWindowSurface.h"
#include "rendering/SurfaceShadowWindow.h"
#include "windows/MessageQueueEntry.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <deque>
#include <memory>
#include <string>
#include <vector>

class MessageQueueWindow final
{
public:
	MessageQueueWindow(ID2D1Factory* d2dFactory, IDWriteFactory* dwriteFactory);
	~MessageQueueWindow();
	MessageQueueWindow(const MessageQueueWindow&) = delete;
	MessageQueueWindow& operator=(const MessageQueueWindow&) = delete;

	HRESULT Attach(HWND window);
	HRESULT Enqueue(std::wstring message, HMONITOR targetMonitor);
	HRESULT BeginActivity(uint64_t token, std::wstring message, HMONITOR targetMonitor);
	HRESULT UpdateActivity(uint64_t token, std::wstring message);
	HRESULT CompleteActivity(
		uint64_t token,
		std::wstring finalMessage,
		bool retainFinalMessage,
		HMONITOR targetMonitor);
	void Show(HMONITOR targetMonitor);
	void Hide() noexcept;
	void Toggle(HMONITOR targetMonitor);
	bool IsVisible() const noexcept { return visible_; }
	bool IsEmpty() const noexcept { return messages_.empty(); }
	HWND WindowHandle() const noexcept { return hwnd_; }
	int PixelWidth() const;
	int PixelHeight() const;
	LRESULT HandleMessage(HWND window, UINT message, WPARAM wParam, LPARAM lParam);

private:
	using Clock = LuvLetterNative::MessageQueueClock;
	struct MessageLayout final
	{
		size_t messageIndex = 0;
		float topDip = 0.0f;
		float widthDip = 1.0f;
		float heightDip = 1.0f;
		float textLeftDip = 0.0f;
		float textTopDip = 0.0f;
		bool showSpinner = false;
		Microsoft::WRL::ComPtr<IDWriteTextLayout> textLayout;
	};

	static constexpr size_t MaximumMessageCount = 6;
	static constexpr size_t MaximumMessageLength = 4096;

	HRESULT EnsureTextFormat();
	HRESULT EnsureResources();
	HRESULT RebuildMessageLayouts();
	void InvalidateMessageLayouts() noexcept;
	void DiscardResources(bool discardSurface) noexcept;
	void RefreshDpiFromWindow();
	void ApplyDpiChange(UINT dpi, const RECT* suggestedRect);
	void UpdateGeometry();
	void UpdatePosition() const;
	void GetSurfaceMetrics(int& width, int& height, float& renderScale) const;
	D2D1_SIZE_F AvailableLayoutSizeDip() const noexcept;
	float WindowWidthDip() const noexcept;
	float WindowHeightDip() const noexcept;
	size_t VisibleMessageCount() const noexcept;
	bool RemoveCompletedMessages(Clock::time_point now);
	bool MakeRoomForMessage() noexcept;
	void ScheduleMessageTimer(Clock::time_point now) noexcept;
	void StopMessageTimer() noexcept;
	void Render(Clock::time_point now);
	static std::wstring NormalizeMessage(std::wstring message);

	HWND hwnd_ = nullptr;
	HMONITOR targetMonitor_ = nullptr;
	UINT dpi_ = LuvLetterNative::DefaultDpi;
	bool visible_ = false;
	bool updatingGeometry_ = false;
	bool messageTimerActive_ = false;
	bool messageLayoutsDirty_ = true;
	float layoutWidthDip_ = 1.0f;
	float layoutHeightDip_ = 1.0f;
	std::deque<LuvLetterNative::MessageQueueEntry> messages_;
	std::vector<MessageLayout> messageLayouts_;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<IDWriteInlineObject> trimmingSign_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> backgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	std::unique_ptr<LuvLetterNative::LayeredWindowSurface> surface_;
	LuvLetterNative::SurfaceShadowWindow shadowWindow_;
};
