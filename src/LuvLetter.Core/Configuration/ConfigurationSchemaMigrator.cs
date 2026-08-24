using System.Text.Json;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

/// <summary>
/// Recognizes serialized schemas and migrates configuration values between schema versions.
/// </summary>
internal static class ConfigurationSchemaMigrator
{
    internal static bool LooksLikeLegacyHotkey(JsonElement root)
    {
        var hasVirtualKey = false;
        var hasInputBox = false;
        foreach (var property in root.EnumerateObject())
        {
            hasVirtualKey |= property.Name.Equals(
                nameof(HotkeyDefinition.VirtualKey),
                StringComparison.OrdinalIgnoreCase);
            hasInputBox |= property.Name.Equals(
                nameof(LuvLetterConfiguration.InputBox),
                StringComparison.OrdinalIgnoreCase);
        }

        return hasVirtualKey && !hasInputBox;
    }

    internal static int ReadSchemaVersion(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(
                    nameof(LuvLetterConfiguration.SchemaVersion),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var version)
                ? version
                : LuvLetterConfiguration.CurrentSchemaVersion;
        }

        return 0;
    }

    internal static LuvLetterConfiguration Migrate(
        LuvLetterConfiguration configuration,
        LuvLetterConfiguration defaults)
    {
        var migrated = configuration;
        if (configuration.SchemaVersion < 3)
        {
            var inputBox = migrated.InputBox;
            if (inputBox is not null
                && inputBox.Size is { } inputSize
                && inputBox.Colors is { } inputColors
                && NearlyEquals(inputSize.CornerRadius, 8.0f)
                && NearlyEquals(inputSize.BorderThickness, 2.0f)
                && IsLegacyOpaqueWhite(inputColors.Border))
            {
                migrated = migrated with
                {
                    InputBox = inputBox with
                    {
                        Size = inputSize with
                        {
                            CornerRadius = defaults.InputBox.Size.CornerRadius,
                            BorderThickness = defaults.InputBox.Size.BorderThickness,
                        },
                        Colors = inputColors with { Border = defaults.InputBox.Colors.Border },
                    },
                };
            }

            var featureWindow = migrated.FeatureWindow;
            if (featureWindow is not null
                && featureWindow.Layout is { } featureLayout
                && featureWindow.Colors is { } featureColors
                && NearlyEquals(featureLayout.CornerRadius, 12.0f)
                && NearlyEquals(featureLayout.BorderThickness, 2.0f)
                && IsLegacyOpaqueWhite(featureColors.Border))
            {
                migrated = migrated with
                {
                    FeatureWindow = featureWindow with
                    {
                        Layout = featureLayout with
                        {
                            CornerRadius = defaults.FeatureWindow.Layout.CornerRadius,
                            BorderThickness = defaults.FeatureWindow.Layout.BorderThickness,
                        },
                        Colors = featureColors with { Border = defaults.FeatureWindow.Colors.Border },
                    },
                };
            }
        }

        if (configuration.SchemaVersion < 4
            && migrated.InputBox is
            {
                Size: { } previousInputSize,
                Colors: { } previousInputColors,
            } previousInputBox
            && IsPreviousInputBoxDefaultBundle(previousInputSize, previousInputColors))
        {
            migrated = migrated with
            {
                InputBox = previousInputBox with
                {
                    Size = defaults.InputBox.Size,
                    Colors = previousInputColors with
                    {
                        Background = defaults.InputBox.Colors.Background,
                        BackgroundOpacity = defaults.InputBox.Colors.BackgroundOpacity,
                    },
                },
            };
        }

        return migrated;
    }

    private static bool IsPreviousInputBoxDefaultBundle(
        InputBoxSizeOptions size,
        InputBoxColorOptions colors) =>
        size.Width == 640
        && size.Height == 44
        && NearlyEquals(size.CornerRadius, 10.0f)
        && NearlyEquals(size.BorderThickness, 1.0f)
        && NearlyEquals(size.FontSize, 20.0f)
        && NearlyEquals(size.HorizontalPadding, 10.0f)
        && NearlyEquals(size.VerticalPadding, 6.0f)
        && NearlyEquals(size.CaretWidth, 2.25f)
        && IsColor(colors.Background, "38F5F5F5")
        && NearlyEquals(colors.BackgroundOpacity, 0.22f);

    private static bool NearlyEquals(float left, float right) =>
        Math.Abs(left - right) < 0.0001f;

    private static bool IsLegacyOpaqueWhite(string? color)
    {
        var normalized = color?.Trim().TrimStart('#');
        return string.Equals(normalized, "FFFFFFFF", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "FFFFFF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsColor(string? color, string expectedArgb) =>
        string.Equals(
            color?.Trim().TrimStart('#'),
            expectedArgb,
            StringComparison.OrdinalIgnoreCase);
}
