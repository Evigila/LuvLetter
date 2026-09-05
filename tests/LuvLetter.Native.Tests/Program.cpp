#include "api/InputBoxApi.h"
#include "configuration/NativeConfigurationSanitizer.h"
#include "rendering/InputBoxAnimator.h"
#include "rendering/SurfaceStyleDefaults.h"
#include "windows/InputCandidateState.h"
#include "windows/MessageQueueEntry.h"

#include <cmath>
#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <utility>
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
		Assert(LuvLetterNative::SurfaceBackgroundColor == 0xE6F0F3F9,
			"All native popup surfaces must use the shared translucent cool-white background.");
		AssertNear(14.0f, LuvLetterNative::SurfaceFontSizeDip,
			"All native popup surfaces must use the shared message font size.");
		Assert(std::wstring{ LuvLetterNative::SurfaceFontFamily } == L"Microsoft YaHei UI",
			"All native popup surfaces must use the shared Microsoft YaHei UI family.");
		AssertNear(20.0f, LuvLetterNative::SurfaceLineHeightDip,
			"Shared line height must leave Microsoft YaHei UI glyphs unclipped.");
		Assert(LUVLETTER_NATIVE_ABI_VERSION == 7, "Native ABI must expose message activities.");
		Assert(LUVLETTER_NATIVE_MAX_INPUT_CANDIDATES == 32,
			"Native ABI must retain the managed candidate-capacity contract.");
		Assert(LUVLETTER_NATIVE_MAX_CANDIDATE_PRIMARY_LENGTH == 512,
			"Native ABI must retain the primary candidate text limit.");
		Assert(LUVLETTER_NATIVE_MAX_CANDIDATE_SECONDARY_LENGTH == 2048,
			"Native ABI must retain the secondary candidate text limit.");
		Assert(sizeof(LuvLetterInputBoxConfig) == 104, "Input config ABI size changed unexpectedly.");
		Assert(sizeof(LuvLetterFeatureWindowConfig) == 88, "Quick Actions config ABI size changed unexpectedly.");
		Assert(sizeof(LuvLetterFeatureItem) == 16, "Quick Action item ABI size changed unexpectedly.");
		Assert(sizeof(LuvLetterInputCandidate) == 32, "Input candidate ABI size changed unexpectedly.");
		Assert(LuvLetterCandidateKindFile == 1, "File candidate kind changed unexpectedly.");
		Assert(LuvLetterCandidateKindCommand == 2, "Command candidate kind changed unexpectedly.");
		Assert(LuvLetterCandidateKindGlobalSearch == 3, "Global Search candidate kind changed unexpectedly.");
		Assert(LuvLetterCandidateActionOpen == 0, "Open candidate action changed unexpectedly.");
		Assert(LuvLetterCandidateActionReveal == 1, "Reveal candidate action changed unexpectedly.");
		Assert(LuvLetterCandidateIconKindGenericFile == 1, "Generic file icon kind changed unexpectedly.");
		Assert(LuvLetterCandidateIconKindFolder == 2, "Folder icon kind changed unexpectedly.");
		Assert(LuvLetterCandidateIconKindImage == 3, "Image icon kind changed unexpectedly.");
		Assert(LuvLetterCandidateIconKindSearch == 10, "Search icon kind changed unexpectedly.");
	}

	void TestConfigurationTypographySanitization()
	{
		auto input = NativeConfigurationSanitizer::DefaultInputBox();
		auto quickActions = NativeConfigurationSanitizer::DefaultQuickActionsWindow();
		AssertNear(LuvLetterNative::SurfaceFontSizeDip, input.fontSize,
			"Default input typography must use the shared surface size.");
		AssertNear(LuvLetterNative::SurfaceFontSizeDip, quickActions.fontSize,
			"Default Quick Actions typography must use the shared surface size.");

		input.fontSize = 22.0f;
		quickActions.fontSize = std::numeric_limits<float>::infinity();
		input = NativeConfigurationSanitizer::SanitizeInputBox(input);
		quickActions = NativeConfigurationSanitizer::SanitizeQuickActionsWindow(quickActions);
		AssertNear(LuvLetterNative::SurfaceFontSizeDip, input.fontSize,
			"Input sanitization must reject a second finite font size.");
		AssertNear(LuvLetterNative::SurfaceFontSizeDip, quickActions.fontSize,
			"Quick Actions sanitization must reject a non-finite font size.");
	}

	void TestMessageActivityTimeline()
	{
		using namespace LuvLetterNative;
		Assert(MessageLifetime == std::chrono::seconds(3),
			"Ordinary messages must use the shortened transient lifetime.");
		const auto started = MessageQueueClock::time_point{};
		MessageQueueEntry activity{
			91,
			L"Indexing",
			started,
			(MessageQueueClock::time_point::max)(),
			true,
		};
		Assert(activity.IsActiveActivity(), "A loading token must be an active message activity.");
		Assert(!activity.HasFiniteLifetime(), "An active message activity must not use the transient lifetime.");
		Assert(!activity.IsRemovalDue(started + std::chrono::hours(24)),
			"An active message activity must remain after an arbitrarily long operation.");

		const auto originalCreatedAt = activity.createdAt;
		activity.Update(L"Indexed 10 files");
		Assert(activity.text == L"Indexed 10 files", "Activity updates must replace text in place.");
		Assert(activity.createdAt == originalCreatedAt, "Activity updates must not restart spinner or entrance time.");

		AssertNear(0.0, CalculateMessageSpinnerRadians(started, started),
			"Spinner must begin at zero radians.");
		AssertNear(3.14159265358979323846,
			CalculateMessageSpinnerRadians(started, started + std::chrono::milliseconds(400)),
			"Spinner must reach half a turn at half its period.");
		AssertNear(0.0,
			CalculateMessageSpinnerRadians(started, started + std::chrono::milliseconds(800)),
			"Spinner phase must wrap after one period.");

		const auto completedAt = started + std::chrono::seconds(10);
		activity.Complete(L"Index ready", true, completedAt);
		Assert(!activity.IsActiveActivity(), "Completing an activity must stop its spinner.");
		Assert(activity.text == L"Index ready", "Completion must retain the supplied final text.");
		Assert(activity.expiresAt == completedAt + MessageLifetime,
			"A final activity message must use the ordinary transient lifetime.");
		Assert(!activity.IsRemovalDue(activity.expiresAt + MessageHideDuration - std::chrono::milliseconds(1)),
			"A completed activity must remain through its exit animation.");
		Assert(activity.IsRemovalDue(activity.expiresAt + MessageHideDuration),
			"A completed activity must be removable at the exit endpoint.");

		MessageQueueEntry dismissed{
			92,
			L"Waiting",
			started,
			(MessageQueueClock::time_point::max)(),
			true,
		};
		dismissed.Complete({}, false, completedAt);
		Assert(dismissed.expiresAt == completedAt,
			"Completion without final text must begin the exit immediately.");
		Assert(dismissed.IsRemovalDue(completedAt + MessageHideDuration),
			"Dismissed activity must be removed after the ordinary exit duration.");
	}

	std::vector<InputCandidateItem> CreateCandidateItems()
	{
		return {
			InputCandidateItem{
				11, LuvLetterCandidateKindFile, LuvLetterCandidateIconKindDocument,
				L"bbb.md", L"C:\\aaa" },
			InputCandidateItem{
				22, LuvLetterCandidateKindCommand, LuvLetterCandidateIconKindCommand,
				L"build", L"Command" },
		};
	}

	void TestCandidateRevisionAndDefaultSelection()
	{
		InputCandidateState state;
		Assert(state.Apply(CreateCandidateItems(), 7, 7), "Current candidate revision must be accepted.");
		Assert(state.Revision() == 7, "Accepted candidate revision must be retained.");
		Assert(state.Items().size() == 2, "Accepted candidates must be retained.");
		Assert(state.SelectedIndex() == 0, "New candidates must select the first activatable item.");

		InputCandidateActivation activation{};
		Assert(state.TryActivate(LuvLetterCandidateActionOpen, activation),
			"Enter must activate the default candidate selection.");
		Assert(activation.token == 11 && activation.action == LuvLetterCandidateActionOpen,
			"The default selection must route the first token with the Open action.");
		Assert(!state.Apply({}, 6, 7), "Stale candidate revision must be rejected.");
		Assert(state.Items().size() == 2, "Rejected stale candidates must not replace the current list.");
		Assert(state.Revision() == 7, "Rejected stale candidates must not change the accepted revision.");
		Assert(state.SelectedIndex() == 0,
			"Rejected stale candidates must not change the current selection.");
	}

	void TestCandidateKeyboardSelectionAndActions()
	{
		InputCandidateState state;
		Assert(state.Apply(CreateCandidateItems(), 9, 9), "Candidate list must be accepted for navigation.");
		Assert(state.SelectedIndex() == 0, "The first candidate must be selected before navigation.");

		Assert(state.MoveSelection(1), "Down must advance the default candidate selection.");
		Assert(state.SelectedIndex() == 1, "Down from the first candidate must select the next candidate.");
		InputCandidateActivation activation{};
		Assert(state.TryActivate(LuvLetterCandidateActionOpen, activation),
			"Enter must activate a selected candidate.");
		Assert(activation.token == 22 && activation.action == LuvLetterCandidateActionOpen,
			"Enter must route the selected token with the Open action.");

		Assert(state.MoveSelection(-1), "Up must return to the first candidate.");
		Assert(state.SelectedIndex() == 0, "Up from the second candidate must select the first candidate.");
		Assert(state.TryActivate(LuvLetterCandidateActionReveal, activation),
			"Shift+Enter must activate a selected candidate.");
		Assert(activation.token == 11 && activation.action == LuvLetterCandidateActionReveal,
			"Shift+Enter must route the selected token with the Reveal action.");

		Assert(state.MoveSelection(-1), "Up at the first candidate must remain navigable.");
		Assert(state.SelectedIndex() == 1, "Up at the first candidate must wrap to the last.");
		Assert(state.MoveSelection(1), "Down at the last candidate must remain navigable.");
		Assert(state.SelectedIndex() == 0, "Down at the last candidate must wrap to the first.");

		Assert(state.Apply({}, 10, 10), "An empty current result must clear candidates.");
		Assert(state.IsEmpty(), "An empty result must leave no candidates.");
		Assert(!state.SelectedIndex().has_value(), "An empty result must clear the default selection.");
		Assert(!state.MoveSelection(1), "Direction keys must not be consumed by an empty candidate list.");
	}

	void TestCandidateSelectionSurvivesSameRevisionRefresh()
	{
		InputCandidateState state;
		Assert(state.Apply(CreateCandidateItems(), 12, 12), "Initial candidate list must be accepted.");
		Assert(state.SelectedIndex() == 0, "The initial list must default to its first candidate.");
		Assert(state.MoveSelection(1), "Down must select the command candidate.");

		auto reordered = CreateCandidateItems();
		std::swap(reordered[0], reordered[1]);
		Assert(state.Apply(std::move(reordered), 12, 12),
			"A same-revision refresh must be accepted.");
		Assert(state.SelectedIndex() == 0,
			"A same-revision refresh must follow the selected token after reordering.");
		InputCandidateActivation activation{};
		Assert(state.TryActivate(LuvLetterCandidateActionOpen, activation)
			&& activation.token == 22,
			"The preserved selection must still activate the same token.");

		auto removed = CreateCandidateItems();
		removed.erase(removed.begin() + 1);
		Assert(state.Apply(std::move(removed), 12, 12),
			"A same-revision refresh without the selected item must be accepted.");
		Assert(state.SelectedIndex() == 0,
			"Selection must fall back to the first candidate when the selected token disappears.");
		Assert(state.TryActivate(LuvLetterCandidateActionOpen, activation)
			&& activation.token == 11,
			"The fallback selection must activate the first remaining candidate.");
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
		{ "Configuration typography sanitization", TestConfigurationTypographySanitization },
		{ "Message activity timeline", TestMessageActivityTimeline },
		{ "Candidate revision and default selection", TestCandidateRevisionAndDefaultSelection },
		{ "Candidate keyboard selection and actions", TestCandidateKeyboardSelectionAndActions },
		{ "Candidate selection survives same-revision refresh", TestCandidateSelectionSurvivesSameRevisionRefresh },
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
