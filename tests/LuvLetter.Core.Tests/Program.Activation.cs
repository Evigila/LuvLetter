using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestDefaultActivationGestures()
    {
        var defaults = LuvLetterConfiguration.Default.ActivationGestures;

        Assert.Equal(
            ActivationGestureKind.DoubleControlPress,
            defaults.InputBox,
            "The command input box must default to double Ctrl.");
        Assert.Equal(
            ActivationGestureKind.ControlTapThenHold,
            defaults.FeatureWindow,
            "The feature window must default to tap Ctrl, then hold Ctrl.");
        Assert.True(defaults.AllowLeftControl, "Left Ctrl should be enabled by default.");
        Assert.True(defaults.AllowRightControl, "Right Ctrl should be enabled by default.");

        var configuration = LuvLetterConfiguration.Default;
        Assert.Equal(560, configuration.InputBox.Size.Width);
        Assert.Equal(32, configuration.InputBox.Size.Height);
        Assert.Equal(14.0f, configuration.InputBox.Size.FontSize);
        Assert.Equal(1.0f, configuration.InputBox.Size.BorderThickness);
        Assert.Equal(8.0f, configuration.InputBox.Size.CornerRadius);
        Assert.Equal(10.0f, configuration.InputBox.Size.HorizontalPadding);
        Assert.Equal(4.0f, configuration.InputBox.Size.VerticalPadding);
        Assert.Equal(2.25f, configuration.InputBox.Size.CaretWidth);
        Assert.Equal("#66FFFFFF", configuration.InputBox.Colors.Border);
        Assert.Equal("#80F5F5F5", configuration.InputBox.Colors.Background);
        Assert.Equal(0.5f, configuration.InputBox.Colors.BackgroundOpacity);
        Assert.Equal(1.0f, configuration.InputBox.Colors.TextOpacity);
        Assert.Equal(1.0f, configuration.FeatureWindow.Layout.BorderThickness);
        Assert.Equal(16.0f, configuration.FeatureWindow.Layout.CornerRadius);
        Assert.Equal("#66FFFFFF", configuration.FeatureWindow.Colors.Border);
        Assert.Equal(1.0f, configuration.FeatureWindow.Colors.TextOpacity);

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureDefaultDoubleTap()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);
        const long firstPressAt = 1_000;
        var firstReleaseAt = firstPressAt + options.TapMaxDurationMs;
        var secondPressAt = firstReleaseAt + options.SecondPressTimeoutMs;
        var secondReleaseAt = secondPressAt + options.TapMaxDurationMs;

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, firstPressAt));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, firstReleaseAt),
            "A tap exactly at the configured maximum duration must remain valid.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, secondPressAt),
            "The second press exactly at its configured timeout must remain valid.");
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleControlUp(ControlKeySide.Left, secondReleaseAt),
            "The default double-Ctrl gesture must request the command input box.");

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(10_000),
            "A completed gesture must not fire again from a stale timer callback.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureDefaultTapThenHold()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Right, 2_000));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 2_040));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Right, 2_150));

        var holdDeadline = machine.NextDeadlineTimestampMs;
        Assert.True(holdDeadline.HasValue, "The second press must arm the hold deadline.");
        Assert.Equal(
            2_150L + options.HoldThresholdMs,
            holdDeadline!.Value,
            "The hold deadline must use the configured threshold.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(holdDeadline.Value - 1),
            "Holding just below the threshold must not activate a feature.");
        Assert.Equal(
            CtrlGestureAction.FeatureWindowRequested,
            machine.HandleTimeout(holdDeadline.Value),
            "The default tap-then-hold gesture must request the feature window at the threshold.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(holdDeadline.Value + 1_000),
            "The hold gesture must fire only once.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, holdDeadline.Value + 1_001));

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureCancellationBoundaries()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);

        machine.HandleControlDown(ControlKeySide.Left, 3_000);
        machine.HandleControlUp(ControlKeySide.Left, 3_020);
        var secondPressDeadline = machine.NextDeadlineTimestampMs;
        Assert.True(secondPressDeadline.HasValue, "The first tap must arm the second-press deadline.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(secondPressDeadline!.Value),
            "Waiting too long for the second press must cancel the sequence.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, secondPressDeadline.Value + 1));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, secondPressDeadline.Value + 20),
            "A Ctrl press after the timeout must start a new sequence, not complete the old one.");

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 4_000);
        machine.HandleControlUp(ControlKeySide.Left, 4_020);
        machine.HandleOtherKey();
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 4_040));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, 4_060),
            "Another key between Ctrl presses must cancel the pending gesture.");

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 5_000);
        machine.HandleOtherKey();
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, 5_020),
            "Another key while Ctrl is down must suppress the gesture until Ctrl is released.");

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 5_100);
        machine.CancelPending();
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 5_110),
            "Cancelling while Ctrl is held must not turn auto-repeat into a new press.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, 5_120),
            "A held Ctrl must remain suppressed until its physical release after cancellation.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureSidesAndAutoRepeat()
    {
        var leftOnly = LuvLetterConfiguration.Default.ActivationGestures with
        {
            AllowLeftControl = true,
            AllowRightControl = false,
        };
        var machine = new CtrlGestureStateMachine(leftOnly);

        Assert.Equal(CtrlGestureAction.None, machine.HandleControlDown(ControlKeySide.Right, 6_000));
        Assert.Equal(CtrlGestureAction.None, machine.HandleControlUp(ControlKeySide.Right, 6_010));
        Assert.Equal(CtrlGestureAction.None, machine.HandleControlDown(ControlKeySide.Right, 6_020));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 6_030),
            "A disabled Ctrl side must never activate a gesture.");

        machine.HandleControlDown(ControlKeySide.Left, 6_100);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 6_110),
            "Auto-repeat during the first press must not count as another physical press.");
        machine.HandleControlUp(ControlKeySide.Left, 6_120);
        machine.HandleControlDown(ControlKeySide.Left, 6_150);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 6_160),
            "Auto-repeat during the second press must not trigger early.");
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleControlUp(ControlKeySide.Left, 6_180));

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 6_300);
        machine.HandleControlUp(ControlKeySide.Left, 6_320);
        machine.HandleControlDown(ControlKeySide.Right, 6_350);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 6_370),
            "A gesture must not mix left and right Ctrl presses.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureConfigurableMapping()
    {
        var swapped = LuvLetterConfiguration.Default.ActivationGestures with
        {
            InputBox = ActivationGestureKind.ControlTapThenHold,
            FeatureWindow = ActivationGestureKind.DoubleControlPress,
        };
        var machine = new CtrlGestureStateMachine(swapped);

        machine.HandleControlDown(ControlKeySide.Left, 7_000);
        machine.HandleControlUp(ControlKeySide.Left, 7_020);
        machine.HandleControlDown(ControlKeySide.Left, 7_100);
        Assert.Equal(
            CtrlGestureAction.FeatureWindowRequested,
            machine.HandleControlUp(ControlKeySide.Left, 7_120),
            "Swapping the mapping must make double Ctrl open the feature window.");

        machine.HandleControlDown(ControlKeySide.Left, 8_000);
        machine.HandleControlUp(ControlKeySide.Left, 8_020);
        machine.HandleControlDown(ControlKeySide.Left, 8_100);
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleTimeout(8_100 + swapped.HoldThresholdMs),
            "Swapping the mapping must make tap-then-hold open the command input box.");

        return Task.CompletedTask;
    }
}
