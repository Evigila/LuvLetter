using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal static class InputBoxSettingsParser
{
    public static bool TryParse(
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
            !SettingsValueParser.TryParseInt(
                input.OffsetX,
                "Command input Offset X",
                out var offsetX,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.OffsetY,
                "Command input Offset Y",
                out var offsetY,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.BottomMargin,
                "Command input bottom margin",
                out var bottomMargin,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.CustomX,
                "Command input Custom X",
                out var customX,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.CustomY,
                "Command input Custom Y",
                out var customY,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.Width,
                "Command input width",
                out var width,
                out error
            )
            || !SettingsValueParser.TryParseInt(
                input.Height,
                "Command input height",
                out var height,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.FontSize,
                "Command input font size",
                out var fontSize,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.CornerRadius,
                "Command input corner radius",
                out var cornerRadius,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.BorderThickness,
                "Command input border thickness",
                out var borderThickness,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.HorizontalPadding,
                "Command input horizontal padding",
                out var horizontalPadding,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.VerticalPadding,
                "Command input vertical padding",
                out var verticalPadding,
                out error
            )
            || !SettingsValueParser.TryParseFloat(
                input.CaretWidth,
                "Command input caret width",
                out var caretWidth,
                out error
            )
            || !SettingsValueParser.TryParseOpacity(
                input.BackgroundOpacity,
                "Command input background opacity",
                out var backgroundOpacity,
                out error
            )
            || !SettingsValueParser.TryParseOpacity(
                input.TextOpacity,
                "Command input text opacity",
                out var textOpacity,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.BorderColor,
                "Command input border color",
                out var borderColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.BackgroundColor,
                "Command input background color",
                out var backgroundColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
                input.TextColor,
                "Command input text color",
                out var textColor,
                out error
            )
            || !SettingsValueParser.TryParseColor(
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
                Background = SettingsValueParser.ApplyOpacityToColor(
                    backgroundColor,
                    backgroundOpacity
                ),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                TextOpacity = textOpacity,
                Caret = caretColor,
            },
            Size = baseline.Size with
            {
                Width = width,
                Height = height,
                FontSize = fontSize,
                CornerRadius = cornerRadius,
                BorderThickness = borderThickness,
                HorizontalPadding = horizontalPadding,
                VerticalPadding = verticalPadding,
                CaretWidth = caretWidth,
            },
        };
        return true;
    }
}
