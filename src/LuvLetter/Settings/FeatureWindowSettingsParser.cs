using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal static class FeatureWindowSettingsParser
{
    public static bool TryParse(
        FeatureWindowConfiguration baseline,
        FeatureWindowSettingsInput input,
        out FeatureWindowConfiguration configuration,
        out string error
    )
    {
        configuration = baseline;
        error = string.Empty;

        if (input.FirstItemVirtualKey is not { } firstItemVirtualKey)
        {
            error = "Select the first feature item number key.";
            return false;
        }

        if (
            !SettingsValueParser.TryParseInt(
                input.ItemsPerPage,
                "Feature items per page",
                out var itemsPerPage,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.CellSize,
                "Feature cell size",
                out var cellSize,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.Gap,
                "Feature gap",
                out var gap,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.CornerRadius,
                "Feature corner radius",
                out var cornerRadius,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.BorderThickness,
                "Feature border thickness",
                out var borderThickness,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.FontSize,
                "Feature font size",
                out var fontSize,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.BottomMargin,
                "Feature bottom margin",
                out var bottomMargin,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.OffsetX,
                "Feature Offset X",
                out var offsetX,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.OffsetY,
                "Feature Offset Y",
                out var offsetY,
                out error
            )
            || !SettingsValueParser.TryParseOpacity(
                input.BackgroundOpacity,
                "Feature background opacity",
                out var backgroundOpacity,
                out error
            )
            || !SettingsValueParser.TryParseOpacity(
                input.TextOpacity,
                "Feature text opacity",
                out var textOpacity,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.BorderColor,
                "Feature border color",
                out var borderColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.AccentColor,
                "Feature accent color",
                out var accentColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.BackgroundColor,
                "Feature background color",
                out var backgroundColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.TextColor,
                "Feature text color",
                out var textColor,
                out error
            )
        )
        {
            return false;
        }

        if (itemsPerPage is < 1 or > FeatureWindowLayoutOptions.MaximumItemsPerPage)
        {
            error = $"Feature items per page must be between 1 and {FeatureWindowLayoutOptions.MaximumItemsPerPage}.";
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
        var hotkeyConflict = FeatureHotkeyRules.FindConflict(hotkeys, itemsPerPage);
        if (hotkeyConflict == FeatureHotkeyConflict.DuplicateAction)
        {
            error = "Feature previous, next, and cancel hotkeys must be different.";
            return false;
        }

        if (hotkeyConflict == FeatureHotkeyConflict.ItemActivationKey)
        {
            error = "Feature navigation and cancel hotkeys cannot overlap the item number keys.";
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
                FontSize = fontSize,
                BottomMargin = bottomMargin,
                OffsetX = offsetX,
                OffsetY = offsetY,
            },
            Colors = baseline.Colors with
            {
                Border = borderColor,
                Background = SettingsValueParser.ApplyOpacityToColor(
                    backgroundColor,
                    backgroundOpacity
                ),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                TextOpacity = textOpacity,
                Accent = accentColor,
            },
            Hotkeys = hotkeys,
        };
        return true;
    }
}
