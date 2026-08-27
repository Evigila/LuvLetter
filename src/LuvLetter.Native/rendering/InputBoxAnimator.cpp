#include "rendering/InputBoxAnimator.h"

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

bool PopupAnimationFrame::IsAnimating() const noexcept
{
	return state == PopupAnimationState::Showing
		|| state == PopupAnimationState::Hiding;
}

bool PopupAnimationFrame::ShouldPresent() const noexcept
{
	return state != PopupAnimationState::Hidden;
}

PopupAnimator::PopupAnimator(const PopupAnimationSettings& settings) noexcept
	: settings_(Sanitize(settings))
{
}

void PopupAnimator::Configure(const PopupAnimationSettings& settings) noexcept
{
	settings_ = Sanitize(settings);
	if (state_ == PopupAnimationState::Showing
		&& settings_.showDurationMilliseconds <= 0.0)
	{
		Reset(true);
	}
	else if (state_ == PopupAnimationState::Hiding
		&& settings_.hideDurationMilliseconds <= 0.0)
	{
		Reset(false);
	}
}

void PopupAnimator::Show() noexcept
{
	SetTargetVisible(true);
}

void PopupAnimator::Hide() noexcept
{
	SetTargetVisible(false);
}

void PopupAnimator::Toggle() noexcept
{
	SetTargetVisible(!TargetVisible());
}

void PopupAnimator::Reset(bool visible) noexcept
{
	progress_ = visible ? 1.0 : 0.0;
	state_ = visible
		? PopupAnimationState::Visible
		: PopupAnimationState::Hidden;
}

PopupAnimationFrame PopupAnimator::Advance(double elapsedMilliseconds) noexcept
{
	if (!std::isfinite(elapsedMilliseconds) || elapsedMilliseconds <= 0.0)
	{
		return Current();
	}

	if (state_ == PopupAnimationState::Showing)
	{
		const auto step = elapsedMilliseconds / settings_.showDurationMilliseconds;
		progress_ = (std::min)(1.0, progress_ + step);
		if (progress_ >= 1.0)
		{
			progress_ = 1.0;
			state_ = PopupAnimationState::Visible;
		}
	}
	else if (state_ == PopupAnimationState::Hiding)
	{
		const auto step = elapsedMilliseconds / settings_.hideDurationMilliseconds;
		progress_ = (std::max)(0.0, progress_ - step);
		if (progress_ <= 0.0)
		{
			progress_ = 0.0;
			state_ = PopupAnimationState::Hidden;
		}
	}

	return Current();
}

PopupAnimationFrame PopupAnimator::Current() const noexcept
{
	const auto progress = (std::clamp)(progress_, 0.0, 1.0);
	const auto motionProgress = EaseOutCubic(progress);
	const auto widthProgress = EaseOutQuart(progress);
	const auto widthScale = settings_.hiddenWidthScale
		+ ((1.0f - settings_.hiddenWidthScale) * static_cast<float>(widthProgress));

	PopupAnimationFrame frame{};
	frame.state = state_;
	frame.linearProgress = progress;
	frame.motionProgress = motionProgress;
	frame.opacity = static_cast<float>(motionProgress);
	frame.widthScale = widthScale;
	frame.verticalOffsetDip = settings_.hiddenVerticalOffsetDip
		* (1.0f - static_cast<float>(motionProgress));
	return frame;
}

const PopupAnimationSettings& PopupAnimator::Settings() const noexcept
{
	return settings_;
}

bool PopupAnimator::TargetVisible() const noexcept
{
	return state_ == PopupAnimationState::Showing
		|| state_ == PopupAnimationState::Visible;
}

PopupAnimationSettings PopupAnimator::Sanitize(
	const PopupAnimationSettings& settings) noexcept
{
	const PopupAnimationSettings defaults{};
	PopupAnimationSettings sanitized{};
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
		-MaximumVerticalOffsetDip,
		MaximumVerticalOffsetDip);
	return sanitized;
}

void PopupAnimator::SetTargetVisible(bool visible) noexcept
{
	if (visible)
	{
		if (progress_ >= 1.0 || settings_.showDurationMilliseconds <= 0.0)
		{
			progress_ = 1.0;
			state_ = PopupAnimationState::Visible;
			return;
		}

		state_ = PopupAnimationState::Showing;
		return;
	}

	if (progress_ <= 0.0 || settings_.hideDurationMilliseconds <= 0.0)
	{
		progress_ = 0.0;
		state_ = PopupAnimationState::Hidden;
		return;
	}

	state_ = PopupAnimationState::Hiding;
}
