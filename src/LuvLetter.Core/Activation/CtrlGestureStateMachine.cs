using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Activation;

public enum ControlKeySide
{
    Left,
    Right,
}

public enum CtrlGestureAction
{
    None,
    CommandInputRequested,
    PopupsDismissRequested,
    QuickActionsRequested,
    MessageQueueToggleRequested,
}

public sealed class CtrlGestureStateMachine
{
    private enum GesturePhase
    {
        Idle,
        FirstPress,
        WaitingForSecondPress,
        SecondPress,
        SuppressedUntilControlReleased,
    }

    private ActivationGestureOptions options;
    private GesturePhase phase;
    private ControlKeySide gestureSide;
    private long pressStartedAtMs;
    private long deadlineAtMs;
    private bool leftControlDown;
    private bool rightControlDown;
    private bool leftAltDown;
    private bool rightAltDown;
    private bool functionOneDown;
    private bool escapeDown;
    private bool backspaceDown;

    public CtrlGestureStateMachine(ActivationGestureOptions options)
    {
        ValidateOptions(options);
        this.options = options;
    }

    public long? NextDeadlineTimestampMs => phase switch
    {
        GesturePhase.FirstPress
            or GesturePhase.WaitingForSecondPress
            or GesturePhase.SecondPress => deadlineAtMs,
        _ => null,
    };

    public CtrlGestureAction HandleControlDown(ControlKeySide side, long timestampMs)
    {
        if (IsControlDown(side))
        {
            // WH_KEYBOARD_LL also reports auto-repeat. A physical press starts only once.
            return CtrlGestureAction.None;
        }

        SetControlDown(side, true);

        if (leftControlDown && rightControlDown)
        {
            SuppressUntilControlsAreReleased();
            return CtrlGestureAction.None;
        }

        ExpireSequenceBeforeInput(timestampMs);

        switch (phase)
        {
            case GesturePhase.Idle:
                if (!IsSideAllowed(side))
                {
                    SuppressUntilControlsAreReleased();
                    break;
                }

                gestureSide = side;
                pressStartedAtMs = timestampMs;
                deadlineAtMs = AddMilliseconds(timestampMs, options.TapMaxDurationMs, 1);
                phase = GesturePhase.FirstPress;
                break;

            case GesturePhase.WaitingForSecondPress:
                if (side != gestureSide || !IsSideAllowed(side))
                {
                    SuppressUntilControlsAreReleased();
                    break;
                }

                pressStartedAtMs = timestampMs;
                deadlineAtMs = AddMilliseconds(timestampMs, options.HoldThresholdMs);
                phase = GesturePhase.SecondPress;
                break;

            case GesturePhase.SuppressedUntilControlReleased:
                break;

            case GesturePhase.FirstPress:
            case GesturePhase.SecondPress:
                // These phases can only receive a different Ctrl here; mixed sides were
                // handled above. Same-side repeats returned at the start of the method.
                SuppressUntilControlsAreReleased();
                break;
        }

        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleControlUp(ControlKeySide side, long timestampMs)
    {
        if (!IsControlDown(side))
        {
            return CtrlGestureAction.None;
        }

        SetControlDown(side, false);

        if (phase == GesturePhase.SuppressedUntilControlReleased)
        {
            ResumeWhenControlsAreReleased();
            return CtrlGestureAction.None;
        }

        ExpireSequenceBeforeInput(timestampMs);

        switch (phase)
        {
            case GesturePhase.FirstPress when side == gestureSide:
                if (ElapsedMilliseconds(pressStartedAtMs, timestampMs) <= options.TapMaxDurationMs)
                {
                    deadlineAtMs = AddMilliseconds(
                        timestampMs,
                        options.SecondPressTimeoutMs,
                        1
                    );
                    phase = GesturePhase.WaitingForSecondPress;
                }
                else
                {
                    ResetPhase();
                }

                break;

            case GesturePhase.SecondPress when side == gestureSide:
                {
                    var pressDurationMs = ElapsedMilliseconds(pressStartedAtMs, timestampMs);
                    ResetPhase();

                    if (pressDurationMs >= options.HoldThresholdMs)
                    {
                        return GetActionFor(ActivationGestureKind.ControlTapThenHold);
                    }

                    if (pressDurationMs <= options.TapMaxDurationMs)
                    {
                        return GetActionFor(ActivationGestureKind.DoubleControlPress);
                    }

                    break;
                }

            case GesturePhase.Idle:
            case GesturePhase.WaitingForSecondPress:
            case GesturePhase.SuppressedUntilControlReleased:
                break;

            case GesturePhase.FirstPress:
            case GesturePhase.SecondPress:
                SuppressUntilControlsAreReleased();
                ResumeWhenControlsAreReleased();
                break;
        }

        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleAltDown(ControlKeySide side)
    {
        SetAltDown(side, true);
        HandleOtherKey();
        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleAltUp(ControlKeySide side)
    {
        SetAltDown(side, false);
        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleFunctionOneDown(bool hasAdditionalModifier = false)
    {
        HandleOtherKey();
        if (functionOneDown)
        {
            return CtrlGestureAction.None;
        }

        functionOneDown = true;
        return (leftAltDown || rightAltDown)
            && !leftControlDown
            && !rightControlDown
            && !hasAdditionalModifier
            ? CtrlGestureAction.QuickActionsRequested
            : CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleFunctionOneUp()
    {
        functionOneDown = false;
        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleBackspaceDown(bool hasAdditionalModifier = false)
    {
        HandleOtherKey();
        if (backspaceDown)
        {
            return CtrlGestureAction.None;
        }

        backspaceDown = true;
        return (leftAltDown || rightAltDown)
            && !leftControlDown
            && !rightControlDown
            && !hasAdditionalModifier
            ? CtrlGestureAction.MessageQueueToggleRequested
            : CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleBackspaceUp()
    {
        backspaceDown = false;
        return CtrlGestureAction.None;
    }

    public CtrlGestureAction HandleEscapeDown()
    {
        HandleOtherKey();
        if (escapeDown)
        {
            return CtrlGestureAction.None;
        }

        escapeDown = true;
        return CtrlGestureAction.PopupsDismissRequested;
    }

    public CtrlGestureAction HandleEscapeUp()
    {
        escapeDown = false;
        return CtrlGestureAction.None;
    }

    public void HandleOtherKey()
    {
        if (phase == GesturePhase.Idle)
        {
            return;
        }

        if (leftControlDown || rightControlDown)
        {
            SuppressUntilControlsAreReleased();
        }
        else
        {
            ResetPhase();
        }
    }

    public CtrlGestureAction HandleTimeout(long timestampMs)
    {
        if (timestampMs < deadlineAtMs)
        {
            return CtrlGestureAction.None;
        }

        switch (phase)
        {
            case GesturePhase.FirstPress:
                SuppressUntilControlsAreReleased();
                break;

            case GesturePhase.WaitingForSecondPress:
                ResetPhase();
                break;

            case GesturePhase.SecondPress:
                SuppressUntilControlsAreReleased();
                return GetActionFor(ActivationGestureKind.ControlTapThenHold);

            case GesturePhase.Idle:
            case GesturePhase.SuppressedUntilControlReleased:
                break;
        }

        return CtrlGestureAction.None;
    }

    public void Update(ActivationGestureOptions options)
    {
        ValidateOptions(options);
        this.options = options;

        if (leftControlDown || rightControlDown)
        {
            SuppressUntilControlsAreReleased();
        }
        else
        {
            ResetPhase();
        }
    }

    public void CancelPending()
    {
        if (leftControlDown || rightControlDown)
        {
            SuppressUntilControlsAreReleased();
        }
        else
        {
            ResetPhase();
        }
    }

    public void Reset()
    {
        leftControlDown = false;
        rightControlDown = false;
        leftAltDown = false;
        rightAltDown = false;
        functionOneDown = false;
        escapeDown = false;
        backspaceDown = false;
        ResetPhase();
    }

    public static void ValidateOptions(ActivationGestureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.TapMaxDurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "TapMaxDurationMs must be greater than zero."
            );
        }

        if (options.SecondPressTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SecondPressTimeoutMs must be greater than zero."
            );
        }

        if (options.HoldThresholdMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "HoldThresholdMs must be greater than zero."
            );
        }

        if (!options.AllowLeftControl && !options.AllowRightControl)
        {
            throw new ArgumentException(
                "At least one Control key must be enabled.",
                nameof(options)
            );
        }

    }

    private void ExpireSequenceBeforeInput(long timestampMs)
    {
        // Tap and inter-press deadlines are inclusive, so an event exactly at their
        // configured limit is still valid. The extra millisecond in those deadlines
        // lets the timeout use one consistent >= comparison.
        if (timestampMs < deadlineAtMs)
        {
            return;
        }

        switch (phase)
        {
            case GesturePhase.FirstPress:
                SuppressUntilControlsAreReleased();
                break;

            case GesturePhase.WaitingForSecondPress:
                ResetPhase();
                break;

            case GesturePhase.SecondPress:
                // HandleControlUp decides between a short press and a qualified hold.
                // Other input cancels explicitly, while the timer has its own path.
                break;

            case GesturePhase.Idle:
            case GesturePhase.SuppressedUntilControlReleased:
                break;
        }
    }

    private bool IsSideAllowed(ControlKeySide side) => side switch
    {
        ControlKeySide.Left => options.AllowLeftControl,
        ControlKeySide.Right => options.AllowRightControl,
        _ => false,
    };

    private CtrlGestureAction GetActionFor(ActivationGestureKind gesture)
    {
        if (gesture == ActivationGestureKind.DoubleControlPress)
        {
            return CtrlGestureAction.CommandInputRequested;
        }

        return CtrlGestureAction.None;
    }

    private bool IsControlDown(ControlKeySide side) => side switch
    {
        ControlKeySide.Left => leftControlDown,
        ControlKeySide.Right => rightControlDown,
        _ => false,
    };

    private void SetControlDown(ControlKeySide side, bool isDown)
    {
        switch (side)
        {
            case ControlKeySide.Left:
                leftControlDown = isDown;
                break;
            case ControlKeySide.Right:
                rightControlDown = isDown;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(side));
        }
    }

    private void SetAltDown(ControlKeySide side, bool isDown)
    {
        switch (side)
        {
            case ControlKeySide.Left:
                leftAltDown = isDown;
                break;
            case ControlKeySide.Right:
                rightAltDown = isDown;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(side));
        }
    }

    private void SuppressUntilControlsAreReleased()
    {
        deadlineAtMs = 0;
        phase = GesturePhase.SuppressedUntilControlReleased;
        ResumeWhenControlsAreReleased();
    }

    private void ResumeWhenControlsAreReleased()
    {
        if (!leftControlDown && !rightControlDown)
        {
            ResetPhase();
        }
    }

    private void ResetPhase()
    {
        phase = GesturePhase.Idle;
        deadlineAtMs = 0;
        pressStartedAtMs = 0;
    }

    private static long ElapsedMilliseconds(long startMs, long endMs) =>
        endMs >= startMs ? endMs - startMs : long.MaxValue;

    private static long AddMilliseconds(long timestampMs, int milliseconds, int extra = 0)
    {
        var increment = (long)milliseconds + extra;
        return timestampMs > long.MaxValue - increment
            ? long.MaxValue
            : timestampMs + increment;
    }
}
