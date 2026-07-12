namespace LuvLetter.Core.Configuration;

public sealed record ActivationGestureOptions
{
    public ActivationGestureKind InputBox { get; init; } =
        ActivationGestureKind.DoubleControlPress;

    public ActivationGestureKind FeatureWindow { get; init; } =
        ActivationGestureKind.ControlTapThenHold;

    public int TapMaxDurationMs { get; init; } = 250;

    public int SecondPressTimeoutMs { get; init; } = 450;

    public int HoldThresholdMs { get; init; } = 600;

    public bool AllowLeftControl { get; init; } = true;

    public bool AllowRightControl { get; init; } = true;
}
