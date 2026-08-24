#pragma once

// The animator owns only deterministic presentation state. It intentionally
// has no dependency on Win32, Direct2D, clocks, timers, or a UI thread.
enum class InputBoxAnimationState
{
	Hidden,
	Showing,
	Visible,
	Hiding,
};

struct InputBoxAnimationSettings final
{
	double showDurationMilliseconds = 180.0;
	double hideDurationMilliseconds = 140.0;
	float hiddenWidthScale = 0.28f;
	float hiddenVerticalOffsetDip = 72.0f;
};

struct InputBoxAnimationFrame final
{
	InputBoxAnimationState state = InputBoxAnimationState::Hidden;
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
class InputBoxAnimator final
{
public:
	explicit InputBoxAnimator(
		const InputBoxAnimationSettings& settings = InputBoxAnimationSettings{}) noexcept;

	void Configure(const InputBoxAnimationSettings& settings) noexcept;
	void Show() noexcept;
	void Hide() noexcept;
	void Toggle() noexcept;
	void Reset(bool visible = false) noexcept;

	InputBoxAnimationFrame Advance(double elapsedMilliseconds) noexcept;
	InputBoxAnimationFrame Current() const noexcept;
	const InputBoxAnimationSettings& Settings() const noexcept;
	bool TargetVisible() const noexcept;

private:
	static InputBoxAnimationSettings Sanitize(
		const InputBoxAnimationSettings& settings) noexcept;
	void SetTargetVisible(bool visible) noexcept;

	InputBoxAnimationSettings settings_;
	InputBoxAnimationState state_ = InputBoxAnimationState::Hidden;
	double progress_ = 0.0;
};
