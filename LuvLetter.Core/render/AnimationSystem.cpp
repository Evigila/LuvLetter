#include "render/AnimationSystem.h"

#include <cmath>

void AnimationSystem::Start(const RECT& from, const RECT& to, ULONGLONG now, DWORD durationMs)
{
	fromRect_ = from;
	toRect_ = to;
	currentRect_ = from;
	startTick_ = now;
	durationMs_ = durationMs;
	active_ = durationMs > 0;

	if (!active_)
	{
		currentRect_ = toRect_;
	}
}

AnimationStep AnimationSystem::Advance(ULONGLONG now)
{
	if (!active_)
	{
		return { currentRect_, true };
	}

	const auto elapsed = now - startTick_;
	if (elapsed >= durationMs_)
	{
		currentRect_ = toRect_;
		active_ = false;
		return { currentRect_, true };
	}

	const auto progress = static_cast<double>(elapsed) / static_cast<double>(durationMs_);
	const auto easedProgress = progress * (2.0 - progress);
	currentRect_ = InterpolateRect(fromRect_, toRect_, easedProgress);
	return { currentRect_, false };
}

bool AnimationSystem::IsActive() const
{
	return active_;
}

const RECT& AnimationSystem::CurrentRect() const
{
	return currentRect_;
}

const RECT& AnimationSystem::TargetRect() const
{
	return toRect_;
}

void AnimationSystem::Complete()
{
	currentRect_ = toRect_;
	active_ = false;
}

RECT AnimationSystem::InterpolateRect(const RECT& from, const RECT& to, double progress)
{
	const auto interpolate = [progress](LONG start, LONG end) {
		return static_cast<LONG>(std::lround(static_cast<double>(start) + ((end - start) * progress)));
	};

	return {
		interpolate(from.left, to.left),
		interpolate(from.top, to.top),
		interpolate(from.right, to.right),
		interpolate(from.bottom, to.bottom),
	};
}
