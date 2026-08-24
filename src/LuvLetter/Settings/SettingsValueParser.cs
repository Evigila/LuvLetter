using System.Globalization;

namespace LuvLetter.Settings;

internal static class SettingsValueParser
{
    public static bool TryParseInt(
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

    public static bool TryParseFloat(
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

    public static bool TryParseOpacity(
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

    public static bool TryParseColor(
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

    public static string ApplyOpacityToColor(string value, float opacity)
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
