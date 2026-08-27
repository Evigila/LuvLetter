#include "api/InputBoxApi.h"
#include "rendering/InputBoxAnimator.h"

#include <cmath>
#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
	constexpr double DoubleTolerance = 1e-9;
	constexpr float FloatTolerance = 1e-5f;

	class TestFailure final : public std::runtime_error
	{
	public:
		explicit TestFailure(const std::string& message)
			: std::runtime_error(message)
		{
		}
	};

	void Assert(bool condition, const std::string& message)
	{
		if (!condition) throw TestFailure(message);
	}

	void AssertNear(double expected, double actual, const std::string& message)
	{
		if (!std::isfinite(expected)
			|| !std::isfinite(actual)
			|| std::abs(expected - actual) > DoubleTolerance)
		{
			throw TestFailure(message);
		}
	}

	void AssertNear(float expected, float actual, const std::string& message)
	{
		if (!std::isfinite(expected)
			|| !std::isfinite(actual)
			|| std::abs(expected - actual) > FloatTolerance)
		{
			throw TestFailure(message);
		}
	}

	void AssertSamePresentation(
		const InputBoxAnimationFrame& expected,
		const InputBoxAnimationFrame& actual,
		const std::string& message)
	{
		AssertNear(expected.linearProgress, actual.linearProgress, message + ": linear progress");
		AssertNear(expected.motionProgress, actual.motionProgress, message + ": motion progress");
		AssertNear(expected.opacity, actual.opacity, message + ": opacity");
		AssertNear(expected.widthScale, actual.widthScale, message + ": width scale");
		AssertNear(expected.verticalOffsetDip, actual.verticalOffsetDip, message + ": vertical offset");
	}

	void TestInitialState()
	{
		InputBoxAnimator animator;
		const auto frame = animator.Current();
		const auto& settings = animator.Settings();

		Assert(frame.state == InputBoxAnimationState::Hidden, "Initial state must be hidden.");
		Assert(!frame.IsAnimating(), "Initial frame must be idle.");
		Assert(!frame.ShouldPresent(), "Initial frame must not be presented.");
		Assert(!animator.TargetVisible(), "Initial target must be hidden.");
		AssertNear(0.0, frame.linearProgress, "Initial linear progress must be zero.");
		AssertNear(0.0, frame.motionProgress, "Initial motion progress must be zero.");
		AssertNear(0.0f, frame.opacity, "Initial opacity must be zero.");
		AssertNear(settings.hiddenWidthScale, frame.widthScale, "Initial width must be constrained.");
		AssertNear(settings.hiddenVerticalOffsetDip, frame.verticalOffsetDip, "Initial offset must be below the final position.");
	}

	void TestAbiContract()
	{
		Assert(LUVLETTER_NATIVE_ABI_VERSION == 2, "Native ABI must expose atomic popup dismissal.");
		Assert(sizeof(LuvLetterInputBoxConfig) == 104, "Input config ABI size changed unexpectedly.");
		Assert(sizeof(LuvLetterFeatureWindowConfig) == 88, "Quick Actions config ABI size changed unexpectedly.");
		Assert(sizeof(LuvLetterFeatureItem) == 16, "Quick Action item ABI size changed unexpectedly.");
	}

	void TestShowHideEndpointsAndRepeatedDirection()
	{
		InputBoxAnimator animator;

		animator.Show();
		const auto showing = animator.Current();
		Assert(showing.state == InputBoxAnimationState::Showing, "Show must start the entrance animation.");
		Assert(showing.ShouldPresent(), "The entrance frame must be presented.");
		animator.Show();
		AssertSamePresentation(showing, animator.Current(), "Repeated Show must not restart or jump");

		const auto visible = animator.Advance(animator.Settings().showDurationMilliseconds);
		Assert(visible.state == InputBoxAnimationState::Visible, "Show must reach Visible at its duration.");
		Assert(!visible.IsAnimating(), "Completed show must be idle.");
		Assert(visible.ShouldPresent(), "Visible frame must be presented.");
		AssertNear(1.0, visible.linearProgress, "Visible linear progress must be one.");
		AssertNear(1.0, visible.motionProgress, "Visible motion progress must be one.");
		AssertNear(1.0f, visible.opacity, "Visible opacity must be one.");
		AssertNear(1.0f, visible.widthScale, "Visible width scale must be one.");
		AssertNear(0.0f, visible.verticalOffsetDip, "Visible vertical offset must be zero.");
		animator.Show();
		Assert(animator.Current().state == InputBoxAnimationState::Visible, "Show while visible must be a no-op.");

		animator.Hide();
		const auto hiding = animator.Current();
		Assert(hiding.state == InputBoxAnimationState::Hiding, "Hide must start the exit animation.");
		Assert(hiding.ShouldPresent(), "The exit frame must remain presented.");
		animator.Hide();
		AssertSamePresentation(hiding, animator.Current(), "Repeated Hide must not restart or jump");

		const auto hidden = animator.Advance(animator.Settings().hideDurationMilliseconds);
		Assert(hidden.state == InputBoxAnimationState::Hidden, "Hide must reach Hidden at its duration.");
		Assert(!hidden.IsAnimating(), "Completed hide must be idle.");
		Assert(!hidden.ShouldPresent(), "Completed hide must not be presented.");
		AssertNear(0.0, hidden.linearProgress, "Hidden linear progress must be zero.");
		AssertNear(0.0f, hidden.opacity, "Hidden opacity must be zero.");
		animator.Hide();
		Assert(animator.Current().state == InputBoxAnimationState::Hidden, "Hide while hidden must be a no-op.");
	}

	void TestBidirectionalReversalContinuity()
	{
		InputBoxAnimator animator;
		animator.Show();
		const auto entranceMidpoint = animator.Advance(63.0);

		animator.Hide();
		const auto reversedToHide = animator.Current();
		Assert(reversedToHide.state == InputBoxAnimationState::Hiding, "Show must reverse to Hide.");
		AssertSamePresentation(entranceMidpoint, reversedToHide, "Show-to-Hide reversal must be continuous");

		const auto movingOut = animator.Advance(17.0);
		Assert(movingOut.linearProgress < reversedToHide.linearProgress, "Hide must reduce progress after reversal.");
		animator.Show();
		const auto reversedToShow = animator.Current();
		Assert(reversedToShow.state == InputBoxAnimationState::Showing, "Hide must reverse to Show.");
		AssertSamePresentation(movingOut, reversedToShow, "Hide-to-Show reversal must be continuous");

		const auto movingIn = animator.Advance(19.0);
		Assert(movingIn.linearProgress > reversedToShow.linearProgress, "Show must increase progress after reversal.");
		animator.Hide();
		const auto secondHide = animator.Current();
		AssertSamePresentation(movingIn, secondHide, "A second Show-to-Hide reversal must remain continuous");
		Assert(animator.Advance(animator.Settings().hideDurationMilliseconds).state == InputBoxAnimationState::Hidden,
			"Reversed exit must still reach Hidden.");
	}

	void TestEasingContract()
	{
		InputBoxAnimator animator;
		animator.Show();
		const auto midpoint = animator.Advance(
			animator.Settings().showDurationMilliseconds / 2.0);

		AssertNear(0.5, midpoint.linearProgress, "The midpoint must retain linear time progress.");
		AssertNear(0.875, midpoint.motionProgress, "Motion must use cubic ease-out.");
		AssertNear(0.875f, midpoint.opacity, "Opacity must follow cubic ease-out.");
		AssertNear(0.955f, midpoint.widthScale, "Width must use quartic ease-out from the default constrained width.");
		AssertNear(9.0f, midpoint.verticalOffsetDip, "Vertical motion must follow cubic ease-out from the default offset.");
	}

	void TestInvalidAndZeroElapsed()
	{
		InputBoxAnimator animator;
		animator.Show();
		const auto before = animator.Advance(35.0);

		AssertSamePresentation(before, animator.Advance(0.0), "Zero elapsed time must be ignored");
		AssertSamePresentation(before, animator.Advance(-1.0), "Negative elapsed time must be ignored");
		AssertSamePresentation(before, animator.Advance(std::numeric_limits<double>::quiet_NaN()), "NaN elapsed time must be ignored");
		AssertSamePresentation(before, animator.Advance(std::numeric_limits<double>::infinity()), "Infinite elapsed time must be ignored");
		Assert(animator.Current().state == InputBoxAnimationState::Showing, "Invalid elapsed time must not change direction.");
	}

	void AssertFrameInRange(
		const InputBoxAnimationFrame& frame,
		const InputBoxAnimationSettings& settings)
	{
		Assert(std::isfinite(frame.linearProgress), "Linear progress must be finite.");
		Assert(std::isfinite(frame.motionProgress), "Motion progress must be finite.");
		Assert(std::isfinite(frame.opacity), "Opacity must be finite.");
		Assert(std::isfinite(frame.widthScale), "Width scale must be finite.");
		Assert(std::isfinite(frame.verticalOffsetDip), "Vertical offset must be finite.");
		Assert(frame.linearProgress >= 0.0 && frame.linearProgress <= 1.0, "Linear progress must stay in range.");
		Assert(frame.motionProgress >= 0.0 && frame.motionProgress <= 1.0, "Motion progress must stay in range.");
		Assert(frame.opacity >= 0.0f && frame.opacity <= 1.0f, "Opacity must stay in range.");
		Assert(frame.widthScale >= settings.hiddenWidthScale && frame.widthScale <= 1.0f, "Width scale must stay in range.");
		Assert(frame.verticalOffsetDip >= 0.0f && frame.verticalOffsetDip <= settings.hiddenVerticalOffsetDip, "Vertical offset must stay in range.");
	}

	void TestFullRangeInvariants()
	{
		InputBoxAnimator animator;
		animator.Show();
		auto previous = animator.Current();
		for (int sample = 0; sample < 1'000; ++sample)
		{
			const auto frame = animator.Advance(0.25);
			AssertFrameInRange(frame, animator.Settings());
			Assert(frame.linearProgress + DoubleTolerance >= previous.linearProgress, "Show progress must be monotonic.");
			Assert(frame.opacity + FloatTolerance >= previous.opacity, "Show opacity must be monotonic.");
			Assert(frame.widthScale + FloatTolerance >= previous.widthScale, "Show width must be monotonic.");
			Assert(frame.verticalOffsetDip <= previous.verticalOffsetDip + FloatTolerance, "Show offset must be monotonic.");
			previous = frame;
		}
		Assert(previous.state == InputBoxAnimationState::Visible, "Range sampling must pass the show endpoint.");

		animator.Hide();
		previous = animator.Current();
		for (int sample = 0; sample < 1'000; ++sample)
		{
			const auto frame = animator.Advance(0.25);
			AssertFrameInRange(frame, animator.Settings());
			Assert(frame.linearProgress <= previous.linearProgress + DoubleTolerance, "Hide progress must be monotonic.");
			Assert(frame.opacity <= previous.opacity + FloatTolerance, "Hide opacity must be monotonic.");
			Assert(frame.widthScale <= previous.widthScale + FloatTolerance, "Hide width must be monotonic.");
			Assert(frame.verticalOffsetDip + FloatTolerance >= previous.verticalOffsetDip, "Hide offset must be monotonic.");
			previous = frame;
		}
		Assert(previous.state == InputBoxAnimationState::Hidden, "Range sampling must pass the hide endpoint.");
	}

	void TestSettingsSanitization()
	{
		InputBoxAnimationSettings extreme{};
		extreme.showDurationMilliseconds = -20.0;
		extreme.hideDurationMilliseconds = 80'000.0;
		extreme.hiddenWidthScale = 4.0f;
		extreme.hiddenVerticalOffsetDip = 8'000.0f;
		InputBoxAnimator clamped(extreme);
		AssertNear(0.0, clamped.Settings().showDurationMilliseconds, "Negative show duration must clamp to zero.");
		AssertNear(60'000.0, clamped.Settings().hideDurationMilliseconds, "Hide duration must clamp to the maximum.");
		AssertNear(1.0f, clamped.Settings().hiddenWidthScale, "Hidden width scale must clamp to one.");
		AssertNear(4'096.0f, clamped.Settings().hiddenVerticalOffsetDip, "Vertical offset must clamp to the maximum.");

		InputBoxAnimationSettings upward{};
		upward.hiddenVerticalOffsetDip = -8'000.0f;
		InputBoxAnimator upwardClamped(upward);
		AssertNear(-4'096.0f, upwardClamped.Settings().hiddenVerticalOffsetDip,
			"Negative vertical offset must support an upward hidden position and clamp to the minimum.");

		InputBoxAnimationSettings nonFinite{};
		nonFinite.showDurationMilliseconds = std::numeric_limits<double>::quiet_NaN();
		nonFinite.hideDurationMilliseconds = std::numeric_limits<double>::infinity();
		nonFinite.hiddenWidthScale = std::numeric_limits<float>::quiet_NaN();
		nonFinite.hiddenVerticalOffsetDip = std::numeric_limits<float>::infinity();
		InputBoxAnimator defaults(nonFinite);
		const InputBoxAnimationSettings expected{};
		AssertNear(expected.showDurationMilliseconds, defaults.Settings().showDurationMilliseconds, "NaN show duration must use the default.");
		AssertNear(expected.hideDurationMilliseconds, defaults.Settings().hideDurationMilliseconds, "Infinite hide duration must use the default.");
		AssertNear(expected.hiddenWidthScale, defaults.Settings().hiddenWidthScale, "NaN width scale must use the default.");
		AssertNear(expected.hiddenVerticalOffsetDip, defaults.Settings().hiddenVerticalOffsetDip, "Infinite offset must use the default.");
	}

	void TestZeroDurationBehavior()
	{
		InputBoxAnimationSettings immediateSettings{};
		immediateSettings.showDurationMilliseconds = 0.0;
		immediateSettings.hideDurationMilliseconds = 0.0;
		InputBoxAnimator immediate(immediateSettings);
		immediate.Show();
		Assert(immediate.Current().state == InputBoxAnimationState::Visible, "Zero-duration Show must complete immediately.");
		immediate.Hide();
		Assert(immediate.Current().state == InputBoxAnimationState::Hidden, "Zero-duration Hide must complete immediately.");

		InputBoxAnimator reconfigured;
		reconfigured.Show();
		reconfigured.Advance(40.0);
		auto settings = reconfigured.Settings();
		settings.showDurationMilliseconds = 0.0;
		reconfigured.Configure(settings);
		Assert(reconfigured.Current().state == InputBoxAnimationState::Visible, "Zero show duration must finish an active entrance.");

		reconfigured.Hide();
		reconfigured.Advance(30.0);
		settings = reconfigured.Settings();
		settings.hideDurationMilliseconds = 0.0;
		reconfigured.Configure(settings);
		Assert(reconfigured.Current().state == InputBoxAnimationState::Hidden, "Zero hide duration must finish an active exit.");
	}
}

int main()
{
	const std::vector<std::pair<std::string, std::function<void()>>> tests
	{
		{ "Native ABI contract", TestAbiContract },
		{ "Initial hidden state", TestInitialState },
		{ "Show/hide endpoints and repeated direction", TestShowHideEndpointsAndRepeatedDirection },
		{ "Bidirectional reversal continuity", TestBidirectionalReversalContinuity },
		{ "Easing midpoint contract", TestEasingContract },
		{ "Invalid and zero elapsed time", TestInvalidAndZeroElapsed },
		{ "Full-range finite and bounded frames", TestFullRangeInvariants },
		{ "Animation settings sanitization", TestSettingsSanitization },
		{ "Zero-duration transitions", TestZeroDurationBehavior },
	};

	std::size_t passed = 0;
	for (const auto& [name, run] : tests)
	{
		try
		{
			run();
			++passed;
			std::cout << "PASS  " << name << '\n';
		}
		catch (const std::exception& exception)
		{
			std::cerr << "FAIL  " << name << '\n';
			std::cerr << exception.what() << '\n';
		}
	}

	std::cout << passed << '/' << tests.size() << " native smoke tests passed.\n";
	return passed == tests.size() ? 0 : 1;
}
