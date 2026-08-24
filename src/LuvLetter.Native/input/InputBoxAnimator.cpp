#include "input/InputBoxAnimator.h"

#include <algorithm>
#include <cmath>

namespace
{
	constexpr double MaximumDurationMilliseconds = 60'000.0;
	constexpr float MaximumVerticalOffsetDip = 4'096.0f;

	double FiniteOr(double value, double fallback) noexcept
	{
		return std::isfinite(value) ? value : fallback;
	}

	float FiniteOr(float value, float fallback) noexcept
	{
		return std::isfinite(value) ? value : fallback;
	}

	double EaseOutCubic(double progress) noexcept
	{
		const auto remaining = 1.0 - progress;
		return 1.0 - (remaining * remaining * remaining);
	}

	double EaseOutQuart(double progress) noexcept
	{
		const auto remaining = 1.0 - progress;
		const auto squared = remaining * remaining;
		return 1.0 - (squared * squared);
	}
}

bool InputBoxAnimationFrame::IsAnimating() const noexcept
{
	return state == InputBoxAnimationState::Showing
		|| state == InputBoxAnimationState::Hiding;
}

bool InputBoxAnimationFrame::ShouldPresent() const noexcept
{
	return state != InputBoxAnimationState::Hidden;
}

InputBoxAnimator::InputBoxAnimator(const InputBoxAnimationSettings& settings) noexcept
	: settings_(Sanitize(settings))
{
}

void InputBoxAnimator::Configure(const InputBoxAnimationSettings& settings) noexcept
{
	settings_ = Sanitize(settings);
	if (state_ == InputBoxAnimationState::Showing
		&& settings_.showDurationMilliseconds <= 0.0)
	{
		Reset(true);
	}
	else if (state_ == InputBoxAnimationState::Hiding
		&& settings_.hideDurationMilliseconds <= 0.0)
	{
		Reset(false);
	}
}

void InputBoxAnimator::Show() noexcept
{
	SetTargetVisible(true);
}

void InputBoxAnimator::Hide() noexcept
{
	SetTargetVisible(false);
}

void InputBoxAnimator::Toggle() noexcept
{
	SetTargetVisible(!TargetVisible());
}

void InputBoxAnimator::Reset(bool visible) noexcept
{
	progress_ = visible ? 1.0 : 0.0;
	state_ = visible
		? InputBoxAnimationState::Visible
		: InputBoxAnimationState::Hidden;
}

InputBoxAnimationFrame InputBoxAnimator::Advance(double elapsedMilliseconds) noexcept
{
	if (!std::isfinite(elapsedMilliseconds) || elapsedMilliseconds <= 0.0)
	{
		return Current();
	}

	if (state_ == InputBoxAnimationState::Showing)
	{
		const auto step = elapsedMilliseconds / settings_.showDurationMilliseconds;
		progress_ = (std::min)(1.0, progress_ + step);
		if (progress_ >= 1.0)
		{
			progress_ = 1.0;
			state_ = InputBoxAnimationState::Visible;
		}
	}
	else if (state_ == InputBoxAnimationState::Hiding)
	{
		const auto step = elapsedMilliseconds / settings_.hideDurationMilliseconds;
		progress_ = (std::max)(0.0, progress_ - step);
		if (progress_ <= 0.0)
		{
			progress_ = 0.0;
			state_ = InputBoxAnimationState::Hidden;
		}
	}

	return Current();
}

InputBoxAnimationFrame InputBoxAnimator::Current() const noexcept
{
	const auto progress = (std::clamp)(progress_, 0.0, 1.0);
	const auto motionProgress = EaseOutCubic(progress);
	const auto widthProgress = EaseOutQuart(progress);
	const auto widthScale = settings_.hiddenWidthScale
		+ ((1.0f - settings_.hiddenWidthScale) * static_cast<float>(widthProgress));

	InputBoxAnimationFrame frame{};
	frame.state = state_;
	frame.linearProgress = progress;
	frame.motionProgress = motionProgress;
	frame.opacity = static_cast<float>(motionProgress);
	frame.widthScale = widthScale;
	frame.verticalOffsetDip = settings_.hiddenVerticalOffsetDip
		* (1.0f - static_cast<float>(motionProgress));
	return frame;
}

const InputBoxAnimationSettings& InputBoxAnimator::Settings() const noexcept
{
	return settings_;
}

bool InputBoxAnimator::TargetVisible() const noexcept
{
	return state_ == InputBoxAnimationState::Showing
		|| state_ == InputBoxAnimationState::Visible;
}

InputBoxAnimationSettings InputBoxAnimator::Sanitize(
	const InputBoxAnimationSettings& settings) noexcept
{
	const InputBoxAnimationSettings defaults{};
	InputBoxAnimationSettings sanitized{};
	sanitized.showDurationMilliseconds = (std::clamp)(
		FiniteOr(settings.showDurationMilliseconds, defaults.showDurationMilliseconds),
		0.0,
		MaximumDurationMilliseconds);
	sanitized.hideDurationMilliseconds = (std::clamp)(
		FiniteOr(settings.hideDurationMilliseconds, defaults.hideDurationMilliseconds),
		0.0,
		MaximumDurationMilliseconds);
	sanitized.hiddenWidthScale = (std::clamp)(
		FiniteOr(settings.hiddenWidthScale, defaults.hiddenWidthScale),
		0.0f,
		1.0f);
	sanitized.hiddenVerticalOffsetDip = (std::clamp)(
		FiniteOr(settings.hiddenVerticalOffsetDip, defaults.hiddenVerticalOffsetDip),
		0.0f,
		MaximumVerticalOffsetDip);
	return sanitized;
}

void InputBoxAnimator::SetTargetVisible(bool visible) noexcept
{
	if (visible)
	{
		if (progress_ >= 1.0 || settings_.showDurationMilliseconds <= 0.0)
		{
			progress_ = 1.0;
			state_ = InputBoxAnimationState::Visible;
			return;
		}

		state_ = InputBoxAnimationState::Showing;
		return;
	}

	if (progress_ <= 0.0 || settings_.hideDurationMilliseconds <= 0.0)
	{
		progress_ = 0.0;
		state_ = InputBoxAnimationState::Hidden;
		return;
	}

	state_ = InputBoxAnimationState::Hiding;
}
