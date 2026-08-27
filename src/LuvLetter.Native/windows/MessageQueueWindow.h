#pragma once

#include "rendering/LayeredWindowSurface.h"

#include <Windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>

#include <chrono>
#include <deque>
#include <memory>
#include <string>

class MessageQueueWindow final
{
public:
	MessageQueueWindow(ID2D1Factory* d2dFactory, IDWriteFactory* dwriteFactory);
	~MessageQueueWindow();
	MessageQueueWindow(const MessageQueueWindow&) = delete;
	MessageQueueWindow& operator=(const MessageQueueWindow&) = delete;

	HRESULT Attach(HWND window);
	void Enqueue(std::wstring message, HMONITOR targetMonitor);
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
	using Clock = std::chrono::steady_clock;

	struct QueuedMessage final
	{
		std::wstring text;
		Clock::time_point expiresAt;
	};

	static constexpr size_t MaximumMessageCount = 6;
	static constexpr size_t MaximumMessageLength = 4096;

	HRESULT EnsureResources();
	void DiscardResources(bool discardSurface) noexcept;
	void RefreshDpiFromWindow();
	void ApplyDpiChange(UINT dpi, const RECT* suggestedRect);
	void UpdateGeometry();
	void UpdatePosition() const;
	void GetSurfaceMetrics(int& width, int& height, float& renderScale) const;
	float WindowHeightDip() const noexcept;
	size_t VisibleMessageCount() const noexcept;
	bool RemoveExpiredMessages(Clock::time_point now);
	void ScheduleExpiryTimer();
	void StopExpiryTimer() noexcept;
	void Render();
	static std::wstring NormalizeMessage(std::wstring message);

	HWND hwnd_ = nullptr;
	HMONITOR targetMonitor_ = nullptr;
	UINT dpi_ = LuvLetterNative::DefaultDpi;
	bool visible_ = false;
	bool updatingGeometry_ = false;
	bool expiryTimerActive_ = false;
	std::deque<QueuedMessage> messages_;

	Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
	Microsoft::WRL::ComPtr<IDWriteFactory> dwriteFactory_;
	Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> renderTarget_;
	Microsoft::WRL::ComPtr<IDWriteTextFormat> textFormat_;
	Microsoft::WRL::ComPtr<IDWriteInlineObject> trimmingSign_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> backgroundBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> borderBrush_;
	Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
	std::unique_ptr<LuvLetterNative::LayeredWindowSurface> surface_;
};
