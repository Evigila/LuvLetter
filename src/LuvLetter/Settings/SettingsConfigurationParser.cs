using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal static class SettingsConfigurationParser
{
    public static bool TryParse(
        LuvLetterConfiguration baseline,
        SettingsEditorInput input,
        out LuvLetterConfiguration configuration,
        out string error
    )
    {
        configuration = baseline;

        if (
            !InputBoxSettingsParser.TryParse(
                baseline.InputBox,
                input.InputBox,
                out var inputBox,
                out error
            )
            || !ActivationGestureSettingsParser.TryParse(
                baseline.ActivationGestures,
                input.ActivationGestures,
                out var activationGestures,
                out error
            )
            || !FeatureWindowSettingsParser.TryParse(
                baseline.FeatureWindow,
                input.FeatureWindow,
                out var featureWindow,
                out error
            )
        )
        {
            return false;
        }

        configuration = baseline with
        {
            InputBox = inputBox,
            ActivationGestures = activationGestures,
            FeatureWindow = featureWindow,
        };
        return true;
    }
}
