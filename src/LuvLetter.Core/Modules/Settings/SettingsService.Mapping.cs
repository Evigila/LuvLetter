using System.Globalization;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Modules.Settings;

/// <summary>
/// Maps settings-editor values to immutable Core configuration snapshots and applies
/// focused editor updates that do not require reading the complete form.
/// </summary>
public sealed partial class SettingsService
{
    public bool TryMap(
        LuvLetterConfiguration baseline,
        SettingsEditorInput input,
        out LuvLetterConfiguration configuration,
        out string error
    )
    {
        configuration = baseline;

        if (
            !TryMapInputBox(
                baseline.InputBox,
                input.InputBox,
                out var inputBox,
                out error
            )
            || !TryMapActivationGestures(
                baseline.ActivationGestures,
                input.ActivationGestures,
                out var activationGestures,
                out error
            )
            || !TryMapQuickActions(
                baseline.QuickActions,
                input.QuickActions,
                out var quickActions,
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
            QuickActions = quickActions,
        };
        return true;
    }

    public LuvLetterConfiguration ReplaceHotkey(
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
        SettingsHotkeyField.QuickActionsPreviousPage => configuration with
        {
            QuickActions = configuration.QuickActions with
            {
                Hotkeys = configuration.QuickActions.Hotkeys with { PreviousPage = hotkey },
            },
        },
        SettingsHotkeyField.QuickActionsNextPage => configuration with
        {
            QuickActions = configuration.QuickActions with
            {
                Hotkeys = configuration.QuickActions.Hotkeys with { NextPage = hotkey },
            },
        },
        SettingsHotkeyField.QuickActionsCancel => configuration with
        {
            QuickActions = configuration.QuickActions with
            {
                Hotkeys = configuration.QuickActions.Hotkeys with { Cancel = hotkey },
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static bool TryMapInputBox(
        InputBoxConfiguration baseline,
        InputBoxSettingsInput input,
        out InputBoxConfiguration configuration,
        out string error
    )
    {
        configuration = baseline;
        error = string.Empty;

        if (input.PositionMode is not { } positionMode)
        {
            error = "Select a command input position mode.";
            return false;
        }

        if (
            !TryParseInt(
                input.OffsetX,
                "Command input Offset X",
                out var offsetX,
                out error
            )
            || !TryParseInt(
                input.OffsetY,
                "Command input Offset Y",
                out var offsetY,
                out error
            )
            || !TryParseInt(
                input.BottomMargin,
                "Command input bottom margin",
                out var bottomMargin,
                out error
            )
            || !TryParseInt(
                input.CustomX,
                "Command input Custom X",
                out var customX,
                out error
            )
            || !TryParseInt(
                input.CustomY,
                "Command input Custom Y",
                out var customY,
                out error
            )
            || !TryParseInt(
                input.Width,
                "Command input width",
                out var width,
                out error
            )
            || !TryParseInt(
                input.Height,
                "Command input height",
                out var height,
                out error
            )
            || !TryParseFloat(
                input.CornerRadius,
                "Command input corner radius",
                out var cornerRadius,
                out error
            )
            || !TryParseFloat(
                input.BorderThickness,
                "Command input border thickness",
                out var borderThickness,
                out error
            )
            || !TryParseFloat(
                input.HorizontalPadding,
                "Command input horizontal padding",
                out var horizontalPadding,
                out error
            )
            || !TryParseFloat(
                input.VerticalPadding,
                "Command input vertical padding",
                out var verticalPadding,
                out error
            )
            || !TryParseFloat(
                input.CaretWidth,
                "Command input caret width",
                out var caretWidth,
                out error
            )
            || !TryParseOpacity(
                input.BackgroundOpacity,
                "Command input background opacity",
                out var backgroundOpacity,
                out error
            )
            || !TryParseOpacity(
                input.TextOpacity,
                "Command input text opacity",
                out var textOpacity,
                out error
            )
            || !TryParseColor(
                input.BorderColor,
                "Command input border color",
                out var borderColor,
                out error
            )
            || !TryParseColor(
                input.BackgroundColor,
                "Command input background color",
                out var backgroundColor,
                out error
            )
            || !TryParseColor(
                input.TextColor,
                "Command input text color",
                out var textColor,
                out error
            )
            || !TryParseColor(
                input.CaretColor,
                "Command input caret color",
                out var caretColor,
                out error
            )
        )
        {
            return false;
        }

        configuration = baseline with
        {
            Placement = baseline.Placement with
            {
                Mode = positionMode,
                OffsetX = offsetX,
                OffsetY = offsetY,
                BottomMargin = bottomMargin,
                CustomX = customX,
                CustomY = customY,
            },
            Colors = baseline.Colors with
            {
                Border = borderColor,
                Background = ApplyOpacityToColor(backgroundColor, backgroundOpacity),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                TextOpacity = textOpacity,
                Caret = caretColor,
            },
            Size = baseline.Size with
            {
                Width = width,
                Height = height,
                FontSize = SurfaceStyleDefaults.FontSize,
                CornerRadius = cornerRadius,
                BorderThickness = borderThickness,
                HorizontalPadding = horizontalPadding,
                VerticalPadding = verticalPadding,
                CaretWidth = caretWidth,
            },
        };
        return true;
    }

    private static bool TryMapActivationGestures(
        ActivationGestureOptions baseline,
        ActivationGestureSettingsInput input,
        out ActivationGestureOptions configuration,
        out string error
    )
    {
        configuration = baseline;
        error = string.Empty;

        if (!input.AllowLeftControl && !input.AllowRightControl)
        {
            error = "Enable at least one Ctrl key.";
            return false;
        }

        if (
            !TryParseInt(
                input.TapMaxDuration,
                "Tap maximum duration",
                out var tapMaxDuration,
                out error
            )
            || !TryParseInt(
                input.SecondPressTimeout,
                "Second press timeout",
                out var secondPressTimeout,
                out error
            )
        )
        {
            return false;
        }

        if (tapMaxDuration <= 0 || secondPressTimeout <= 0)
        {
            error = "Double-Ctrl timings must be greater than zero milliseconds.";
            return false;
        }

        configuration = baseline with
        {
            InputBox = ActivationGestureKind.DoubleControlPress,
            QuickActions = ActivationGestureKind.ControlTapThenHold,
            TapMaxDurationMs = tapMaxDuration,
            SecondPressTimeoutMs = secondPressTimeout,
            AllowLeftControl = input.AllowLeftControl,
            AllowRightControl = input.AllowRightControl,
        };
        return true;
    }

    private static bool TryMapQuickActions(
        QuickActionsConfiguration baseline,
        QuickActionsSettingsInput input,
        out QuickActionsConfiguration configuration,
        out string error
    )
    {
        configuration = baseline;
        error = string.Empty;

        if (input.FirstItemVirtualKey is not { } firstItemVirtualKey)
        {
            error = "Select the first quick action item number key.";
            return false;
        }

        if (
            !TryParseInt(
                input.ItemsPerPage,
                "QuickActions items per page",
                out var itemsPerPage,
                out error
            )
            || !TryParseInt(
                input.CellSize,
                "QuickActions cell size",
                out var cellSize,
                out error
            )
            || !TryParseInt(
                input.Gap,
                "QuickActions gap",
                out var gap,
                out error
            )
            || !TryParseFloat(
                input.CornerRadius,
                "QuickActions corner radius",
                out var cornerRadius,
                out error
            )
            || !TryParseFloat(
                input.BorderThickness,
                "QuickActions border thickness",
                out var borderThickness,
                out error
            )
            || !TryParseInt(
                input.BottomMargin,
                "QuickActions top margin",
                out var bottomMargin,
                out error
            )
            || !TryParseInt(
                input.OffsetX,
                "QuickActions Offset X",
                out var offsetX,
                out error
            )
            || !TryParseInt(
                input.OffsetY,
                "QuickActions Offset Y",
                out var offsetY,
                out error
            )
            || !TryParseOpacity(
                input.BackgroundOpacity,
                "QuickActions background opacity",
                out var backgroundOpacity,
                out error
            )
            || !TryParseOpacity(
                input.TextOpacity,
                "QuickActions text opacity",
                out var textOpacity,
                out error
            )
            || !TryParseColor(
                input.BorderColor,
                "QuickActions border color",
                out var borderColor,
                out error
            )
            || !TryParseColor(
                input.AccentColor,
                "QuickActions accent color",
                out var accentColor,
                out error
            )
            || !TryParseColor(
                input.BackgroundColor,
                "QuickActions background color",
                out var backgroundColor,
                out error
            )
            || !TryParseColor(
                input.TextColor,
                "QuickActions text color",
                out var textColor,
                out error
            )
        )
        {
            return false;
        }

        if (itemsPerPage is < 1 or > QuickActionsLayoutOptions.MaximumItemsPerPage)
        {
            error = $"QuickActions items per page must be between 1 and {QuickActionsLayoutOptions.MaximumItemsPerPage}.";
            return false;
        }

        if (
            firstItemVirtualKey is >= 0x30 and <= 0x39
            && firstItemVirtualKey + itemsPerPage - 1 > 0x39
        )
        {
            error = "The first item number and items per page must stay within keys 0-9.";
            return false;
        }

        var hotkeys = baseline.Hotkeys with { FirstItemVirtualKey = firstItemVirtualKey };
        var hotkeyConflict = QuickActionHotkeyRules.FindConflict(hotkeys, itemsPerPage);
        if (hotkeyConflict == QuickActionHotkeyConflict.DuplicateAction)
        {
            error = "QuickActions previous, next, and cancel hotkeys must be different.";
            return false;
        }

        if (hotkeyConflict == QuickActionHotkeyConflict.ItemActivationKey)
        {
            error = "QuickActions navigation and cancel hotkeys cannot overlap the item number keys.";
            return false;
        }

        configuration = baseline with
        {
            Layout = baseline.Layout with
            {
                ItemsPerPage = itemsPerPage,
                CellSize = cellSize,
                Gap = gap,
                CornerRadius = cornerRadius,
                BorderThickness = borderThickness,
                FontSize = SurfaceStyleDefaults.FontSize,
                BottomMargin = bottomMargin,
                OffsetX = offsetX,
                OffsetY = offsetY,
            },
            Colors = baseline.Colors with
            {
                Border = borderColor,
                Background = ApplyOpacityToColor(backgroundColor, backgroundOpacity),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                TextOpacity = textOpacity,
                Accent = accentColor,
            },
            Hotkeys = hotkeys,
        };
        return true;
    }

    private static bool TryParseInt(
        string text,
        string label,
        out int value,
        out string error
    )
    {
        if (
            int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value
            )
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be an integer.";
        return false;
    }

    private static bool TryParseFloat(
        string text,
        string label,
        out float value,
        out string error
    )
    {
        if (
            float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value
            )
            && float.IsFinite(value)
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be a finite number.";
        return false;
    }

    private static bool TryParseOpacity(
        string text,
        string label,
        out float value,
        out string error
    )
    {
        if (!TryParseFloat(text, label, out value, out error))
        {
            return false;
        }

        if (value is < 0.0f or > 1.0f)
        {
            error = $"{label} must be between 0 and 1.";
            return false;
        }

        return true;
    }

    private static bool TryParseColor(
        string text,
        string label,
        out string value,
        out string error
    )
    {
        value = text.Trim();
        var hex = value.TrimStart('#');
        if (
            hex.Length is 6 or 8
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be #RRGGBB or #AARRGGBB.";
        return false;
    }

    private static string ApplyOpacityToColor(string value, float opacity)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        var alpha = (int)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f);
        return $"#{alpha:X2}{hex[^6..]}";
    }
}

public enum SettingsHotkeyField
{
    InputSubmit,
    InputCancel,
    InputBackspace,
    QuickActionsPreviousPage,
    QuickActionsNextPage,
    QuickActionsCancel,
}
