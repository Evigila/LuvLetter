using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal static class ActivationGestureSettingsParser
{
    public static bool TryParse(
        ActivationGestureOptions baseline,
        ActivationGestureSettingsInput input,
        out ActivationGestureOptions configuration,
        out string error
    )
    {
        configuration = baseline;
        error = string.Empty;

        if (input.InputBoxGesture is not { } inputBoxGesture)
        {
            error = "Select a command input activation gesture.";
            return false;
        }

        if (input.FeatureWindowGesture is not { } featureWindowGesture)
        {
            error = "Select a feature window activation gesture.";
            return false;
        }

        if (inputBoxGesture == featureWindowGesture)
        {
            error = "Command input and feature window must use different gestures.";
            return false;
        }

        if (!input.AllowLeftControl && !input.AllowRightControl)
        {
            error = "Enable at least one Ctrl key.";
            return false;
        }

        if (
            !SettingsValueParser.TryParseInt(
                input.TapMaxDuration,
                "Tap maximum duration",
                out var tapMaxDuration,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.SecondPressTimeout,
                "Second press timeout",
                out var secondPressTimeout,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.HoldThreshold,
                "Hold threshold",
                out var holdThreshold,
                out error
            )
        )
        {
            return false;
        }

        if (tapMaxDuration <= 0 || secondPressTimeout <= 0 || holdThreshold <= 0)
        {
            error = "Gesture timings must be greater than zero milliseconds.";
            return false;
        }

        configuration = baseline with
        {
            InputBox = inputBoxGesture,
            FeatureWindow = featureWindowGesture,
            TapMaxDurationMs = tapMaxDuration,
            SecondPressTimeoutMs = secondPressTimeout,
            HoldThresholdMs = holdThreshold,
            AllowLeftControl = input.AllowLeftControl,
            AllowRightControl = input.AllowRightControl,
        };
        return true;
    }
}
