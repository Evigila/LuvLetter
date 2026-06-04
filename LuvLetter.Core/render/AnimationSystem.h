#pragma once

#include <Windows.h>

struct AnimationStep
{
	RECT windowRect{};
	bool completed = true;
};

class AnimationSystem
{
public:
	void Start(const RECT& from, const RECT& to, ULONGLONG now, DWORD durationMs);
	AnimationStep Advance(ULONGLONG now);
	bool IsActive() const;
	const RECT& CurrentRect() const;
	const RECT& TargetRect() const;
	void Complete();

private:
	static RECT InterpolateRect(const RECT& from, const RECT& to, double progress);

	RECT fromRect_{};
	RECT toRect_{};
	RECT currentRect_{};
	ULONGLONG startTick_ = 0;
	DWORD durationMs_ = 0;
	bool active_ = false;
};
