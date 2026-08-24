using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Settings;

internal enum SettingsHotkeyField
{
    InputSubmit,
    InputCancel,
    InputBackspace,
    FeaturePreviousPage,
    FeatureNextPage,
    FeatureCancel,
}

internal static class SettingsHotkeyEditor
{
    public static LuvLetterConfiguration Replace(
        LuvLetterConfiguration configuration,
        SettingsHotkeyField field,
        HotkeyDefinition hotkey
    ) => field switch
    {
        SettingsHotkeyField.InputSubmit => configuration with
        {
            InputBox = configuration.InputBox with
            {
                Hotkeys = configuration.InputBox.Hotkeys with { Submit = hotkey },
            },
        },
        SettingsHotkeyField.InputCancel => configuration with
        {
            InputBox = configuration.InputBox with
            {
                Hotkeys = configuration.InputBox.Hotkeys with { Cancel = hotkey },
            },
        },
        SettingsHotkeyField.InputBackspace => configuration with
        {
            InputBox = configuration.InputBox with
            {
                Hotkeys = configuration.InputBox.Hotkeys with { Backspace = hotkey },
            },
        },
        SettingsHotkeyField.FeaturePreviousPage => configuration with
        {
            FeatureWindow = configuration.FeatureWindow with
            {
                Hotkeys = configuration.FeatureWindow.Hotkeys with { PreviousPage = hotkey },
            },
        },
        SettingsHotkeyField.FeatureNextPage => configuration with
        {
            FeatureWindow = configuration.FeatureWindow with
            {
                Hotkeys = configuration.FeatureWindow.Hotkeys with { NextPage = hotkey },
            },
        },
        SettingsHotkeyField.FeatureCancel => configuration with
        {
            FeatureWindow = configuration.FeatureWindow with
            {
                Hotkeys = configuration.FeatureWindow.Hotkeys with { Cancel = hotkey },
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}
