using System.Text.Json;
using System.Text.Json.Nodes;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

/// <summary>
/// Recognizes serialized schemas and migrates configuration values between schema versions.
/// </summary>
internal static class ConfigurationSchemaMigrator
{
    private const string LegacyQuickActionsPropertyName = "FeatureWindow";

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

    internal static JsonObject MigrateDocument(JsonElement root)
    {
        ValidateLegacyProperty(
            root,
            LegacyQuickActionsPropertyName,
            nameof(LuvLetterConfiguration.QuickActions),
            "settings root");
        var activationProperty = FindSingleProperty(
            root,
            nameof(LuvLetterConfiguration.ActivationGestures),
            "settings root");
        if (activationProperty is { Value.ValueKind: JsonValueKind.Object })
        {
            ValidateLegacyProperty(
                activationProperty.Value.Value,
                LegacyQuickActionsPropertyName,
                nameof(ActivationGestureOptions.QuickActions),
                "activation gestures");
        }

        var migrated = JsonNode.Parse(root.GetRawText()) as JsonObject
            ?? throw new JsonException("The settings root must be a JSON object.");

        RenameLegacyProperty(
            root,
            migrated,
            LegacyQuickActionsPropertyName,
            nameof(LuvLetterConfiguration.QuickActions),
            "settings root");

        if (activationProperty is { Value.ValueKind: JsonValueKind.Object }
            && FindNodeProperty(
                migrated,
                nameof(LuvLetterConfiguration.ActivationGestures)) is JsonObject activationNode)
        {
            RenameLegacyProperty(
                activationProperty.Value.Value,
                activationNode,
                LegacyQuickActionsPropertyName,
                nameof(ActivationGestureOptions.QuickActions),
                "activation gestures");
        }

        return migrated;
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

            var quickActions = migrated.QuickActions;
            if (quickActions is not null
                && quickActions.Layout is { } quickActionsLayout
                && quickActions.Colors is { } quickActionsColors
                && NearlyEquals(quickActionsLayout.CornerRadius, 12.0f)
                && NearlyEquals(quickActionsLayout.BorderThickness, 2.0f)
                && IsLegacyOpaqueWhite(quickActionsColors.Border))
            {
                migrated = migrated with
                {
                    QuickActions = quickActions with
                    {
                        Layout = quickActionsLayout with
                        {
                            CornerRadius = defaults.QuickActions.Layout.CornerRadius,
                            BorderThickness = defaults.QuickActions.Layout.BorderThickness,
                        },
                        Colors = quickActionsColors with
                        {
                            Border = defaults.QuickActions.Colors.Border,
                        },
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

    private static void RenameLegacyProperty(
        JsonElement source,
        JsonObject target,
        string legacyName,
        string canonicalName,
        string location)
    {
        var legacy = FindSingleProperty(source, legacyName, location);
        var canonical = FindSingleProperty(source, canonicalName, location);
        if (legacy is not null && canonical is not null)
        {
            throw new JsonException(
                $"The {location} cannot contain both '{legacyName}' and '{canonicalName}'.");
        }

        if (legacy is null)
        {
            return;
        }

        var legacyNode = FindNodeProperty(target, legacy.Value.Name);
        RemoveNodeProperty(target, legacy.Value.Name);
        target[canonicalName] = legacyNode?.DeepClone();
    }

    private static void ValidateLegacyProperty(
        JsonElement source,
        string legacyName,
        string canonicalName,
        string location)
    {
        var legacy = FindSingleProperty(source, legacyName, location);
        var canonical = FindSingleProperty(source, canonicalName, location);
        if (legacy is not null && canonical is not null)
        {
            throw new JsonException(
                $"The {location} cannot contain both '{legacyName}' and '{canonicalName}'.");
        }
    }

    private static JsonProperty? FindSingleProperty(
        JsonElement source,
        string propertyName,
        string location)
    {
        JsonProperty? match = null;
        foreach (var property in source.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                throw new JsonException(
                    $"The {location} contains duplicate '{propertyName}' properties.");
            }

            match = property;
        }

        return match;
    }

    private static JsonNode? FindNodeProperty(JsonObject source, string propertyName)
    {
        foreach (var property in source)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static void RemoveNodeProperty(JsonObject source, string propertyName)
    {
        var actualName = source
            .Select(static property => property.Key)
            .FirstOrDefault(name => name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        if (actualName is not null)
        {
            source.Remove(actualName);
        }
    }
}
