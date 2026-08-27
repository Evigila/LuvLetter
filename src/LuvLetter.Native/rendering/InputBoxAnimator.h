#pragma once

// The animator owns only deterministic presentation state. It intentionally
// has no dependency on Win32, Direct2D, clocks, timers, or a UI thread.
enum class PopupAnimationState
{
	Hidden,
	Showing,
	Visible,
	Hiding,
};

struct PopupAnimationSettings final
{
	double showDurationMilliseconds = 180.0;
	double hideDurationMilliseconds = 140.0;
	float hiddenWidthScale = 0.28f;
	float hiddenVerticalOffsetDip = 72.0f;
};

struct PopupAnimationFrame final
{
	PopupAnimationState state = PopupAnimationState::Hidden;
	double linearProgress = 0.0;
	double motionProgress = 0.0;
	float opacity = 0.0f;
	float widthScale = 0.28f;
	float verticalOffsetDip = 72.0f;

	bool IsAnimating() const noexcept;
	bool ShouldPresent() const noexcept;
};

// Models one reversible hidden-to-visible path. Show and Hide only change the
// direction in which linearProgress travels, so reversing an in-flight
// animation cannot change the current frame's visual values.
class PopupAnimator final
{
public:
	explicit PopupAnimator(
		const PopupAnimationSettings& settings = PopupAnimationSettings{}) noexcept;

	void Configure(const PopupAnimationSettings& settings) noexcept;
	void Show() noexcept;
	void Hide() noexcept;
	void Toggle() noexcept;
	void Reset(bool visible = false) noexcept;

	PopupAnimationFrame Advance(double elapsedMilliseconds) noexcept;
	PopupAnimationFrame Current() const noexcept;
	const PopupAnimationSettings& Settings() const noexcept;
	bool TargetVisible() const noexcept;

private:
	static PopupAnimationSettings Sanitize(
		const PopupAnimationSettings& settings) noexcept;
	void SetTargetVisible(bool visible) noexcept;

	PopupAnimationSettings settings_;
	PopupAnimationState state_ = PopupAnimationState::Hidden;
	double progress_ = 0.0;
};

// Source-compatible aliases for the existing Native animation tests and any
// callers compiled against the pre-generalized class names.
using InputBoxAnimationState = PopupAnimationState;
using InputBoxAnimationSettings = PopupAnimationSettings;
using InputBoxAnimationFrame = PopupAnimationFrame;
using InputBoxAnimator = PopupAnimator;
