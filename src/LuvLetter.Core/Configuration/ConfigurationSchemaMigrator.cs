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

        if (configuration.SchemaVersion < 7
            && migrated.QuickActions is
            {
                Layout: { } previousQuickActionsLayout,
                Colors: { } previousQuickActionsColors,
            } previousQuickActions)
        {
            var usesPreviousCornerRadius = NearlyEquals(
                previousQuickActionsLayout.CornerRadius,
                16.0f);
            var usesPreviousBackground = IsColor(
                    previousQuickActionsColors.Background,
                    "38F5F5F5")
                && NearlyEquals(previousQuickActionsColors.BackgroundOpacity, 0.22f);
            if (usesPreviousCornerRadius || usesPreviousBackground)
            {
                migrated = migrated with
                {
                    QuickActions = previousQuickActions with
                    {
                        Layout = usesPreviousCornerRadius
                            ? previousQuickActionsLayout with
                            {
                                CornerRadius = defaults.QuickActions.Layout.CornerRadius,
                            }
                            : previousQuickActionsLayout,
                        Colors = usesPreviousBackground
                            ? previousQuickActionsColors with
                            {
                                Background = defaults.QuickActions.Colors.Background,
                                BackgroundOpacity = defaults.QuickActions.Colors.BackgroundOpacity,
                            }
                            : previousQuickActionsColors,
                    },
                };
            }
        }

        if (configuration.SchemaVersion < 8)
        {
            if (migrated.InputBox is { Colors: { } previousThemeInputColors } themedInputBox)
            {
                var usesPreviousBackground = IsPreviousSurfaceBackground(
                    previousThemeInputColors);
                var usesUnifiedBackground = usesPreviousBackground
                    || IsUnifiedSurfaceBackground(
                        previousThemeInputColors.Background,
                        previousThemeInputColors.BackgroundOpacity,
                        defaults.InputBox.Colors.Background,
                        defaults.InputBox.Colors.BackgroundOpacity);
                if (usesUnifiedBackground)
                {
                    migrated = migrated with
                    {
                        InputBox = themedInputBox with
                        {
                            Colors = previousThemeInputColors with
                            {
                                Border = IsColor(previousThemeInputColors.Border, "66FFFFFF")
                                    ? defaults.InputBox.Colors.Border
                                    : previousThemeInputColors.Border,
                                Background = usesPreviousBackground
                                    ? defaults.InputBox.Colors.Background
                                    : previousThemeInputColors.Background,
                                BackgroundOpacity = usesPreviousBackground
                                    ? defaults.InputBox.Colors.BackgroundOpacity
                                    : previousThemeInputColors.BackgroundOpacity,
                                Text = IsPreviousSurfaceContent(
                                        previousThemeInputColors.Text,
                                        previousThemeInputColors.TextOpacity)
                                    ? defaults.InputBox.Colors.Text
                                    : previousThemeInputColors.Text,
                                TextOpacity = IsPreviousSurfaceContent(
                                        previousThemeInputColors.Text,
                                        previousThemeInputColors.TextOpacity)
                                    ? defaults.InputBox.Colors.TextOpacity
                                    : previousThemeInputColors.TextOpacity,
                                Caret = IsColor(previousThemeInputColors.Caret, "FFFFFFFF")
                                    ? defaults.InputBox.Colors.Caret
                                    : previousThemeInputColors.Caret,
                            },
                        },
                    };
                }
            }

            if (migrated.QuickActions is
                { Colors: { } previousThemeQuickActionsColors } themedQuickActions)
            {
                var usesPreviousBackground = IsPreviousSurfaceBackground(
                    previousThemeQuickActionsColors);
                var usesUnifiedBackground = usesPreviousBackground
                    || IsUnifiedSurfaceBackground(
                        previousThemeQuickActionsColors.Background,
                        previousThemeQuickActionsColors.BackgroundOpacity,
                        defaults.QuickActions.Colors.Background,
                        defaults.QuickActions.Colors.BackgroundOpacity);
                if (usesUnifiedBackground)
                {
                    migrated = migrated with
                    {
                        QuickActions = themedQuickActions with
                        {
                            Colors = previousThemeQuickActionsColors with
                            {
                                Border = IsColor(
                                        previousThemeQuickActionsColors.Border,
                                        "66FFFFFF")
                                    ? defaults.QuickActions.Colors.Border
                                    : previousThemeQuickActionsColors.Border,
                                Background = usesPreviousBackground
                                    ? defaults.QuickActions.Colors.Background
                                    : previousThemeQuickActionsColors.Background,
                                BackgroundOpacity = usesPreviousBackground
                                    ? defaults.QuickActions.Colors.BackgroundOpacity
                                    : previousThemeQuickActionsColors.BackgroundOpacity,
                                Text = IsPreviousSurfaceContent(
                                        previousThemeQuickActionsColors.Text,
                                        previousThemeQuickActionsColors.TextOpacity)
                                    ? defaults.QuickActions.Colors.Text
                                    : previousThemeQuickActionsColors.Text,
                                TextOpacity = IsPreviousSurfaceContent(
                                        previousThemeQuickActionsColors.Text,
                                        previousThemeQuickActionsColors.TextOpacity)
                                    ? defaults.QuickActions.Colors.TextOpacity
                                    : previousThemeQuickActionsColors.TextOpacity,
                                Accent = IsColor(
                                        previousThemeQuickActionsColors.Accent,
                                        "FFFFFFFF")
                                    ? defaults.QuickActions.Colors.Accent
                                    : previousThemeQuickActionsColors.Accent,
                            },
                        },
                    };
                }
            }
        }

        if (configuration.SchemaVersion < 9
            && migrated.InputBox is
            {
                Size: { } legacyDarkInputSize,
                Colors: { } legacyDarkInputColors,
            } legacyDarkInputBox
            && IsPreviousInputBoxSizeBundle(legacyDarkInputSize)
            && IsLegacyDarkInputTheme(legacyDarkInputColors, defaults.InputBox.Colors))
        {
            migrated = migrated with
            {
                InputBox = legacyDarkInputBox with
                {
                    Size = legacyDarkInputSize with
                    {
                        CornerRadius = defaults.InputBox.Size.CornerRadius,
                        BorderThickness = defaults.InputBox.Size.BorderThickness,
                    },
                    Colors = defaults.InputBox.Colors,
                },
            };
        }

        if (configuration.SchemaVersion < 10)
        {
            if (migrated.InputBox is { Colors: { } silverInputColors } silverInputBox
                && IsPreviousSilverSurfaceBackground(
                    silverInputColors.Background,
                    silverInputColors.BackgroundOpacity))
            {
                migrated = migrated with
                {
                    InputBox = silverInputBox with
                    {
                        Colors = silverInputColors with
                        {
                            Background = defaults.InputBox.Colors.Background,
                            BackgroundOpacity = defaults.InputBox.Colors.BackgroundOpacity,
                        },
                    },
                };
            }

            if (migrated.QuickActions is
                { Colors: { } silverQuickActionsColors } silverQuickActions
                && IsPreviousSilverSurfaceBackground(
                    silverQuickActionsColors.Background,
                    silverQuickActionsColors.BackgroundOpacity))
            {
                migrated = migrated with
                {
                    QuickActions = silverQuickActions with
                    {
                        Colors = silverQuickActionsColors with
                        {
                            Background = defaults.QuickActions.Colors.Background,
                            BackgroundOpacity = defaults.QuickActions.Colors.BackgroundOpacity,
                        },
                    },
                };
            }
        }

        return migrated;
    }

    private static bool IsPreviousSilverSurfaceBackground(string? color, float opacity) =>
        (IsColor(color, "FFC0C0C0") || IsColor(color, "C0C0C0"))
        && NearlyEquals(opacity, 1.0f);

    private static bool IsPreviousSurfaceBackground(InputBoxColorOptions colors) =>
        IsColor(colors.Background, "80F5F5F5")
        && NearlyEquals(colors.BackgroundOpacity, 0.5f);

    private static bool IsPreviousSurfaceBackground(QuickActionsColorOptions colors) =>
        IsColor(colors.Background, "80F5F5F5")
        && NearlyEquals(colors.BackgroundOpacity, 0.5f);

    private static bool IsPreviousSurfaceContent(string? color, float opacity) =>
        IsColor(color, "FFFFFFFF") && NearlyEquals(opacity, 1.0f);

    private static bool IsUnifiedSurfaceBackground(
        string? color,
        float opacity,
        string defaultColor,
        float defaultOpacity) =>
        IsSameColor(color, defaultColor) && NearlyEquals(opacity, defaultOpacity);

    private static bool IsPreviousInputBoxDefaultBundle(
        InputBoxSizeOptions size,
        InputBoxColorOptions colors) =>
        IsPreviousInputBoxSizeBundle(size)
        && IsPreviousLightInputBackground(colors);

    private static bool IsPreviousInputBoxSizeBundle(InputBoxSizeOptions size) =>
        size.Width == 640
        && size.Height == 44
        && NearlyEquals(size.CornerRadius, 10.0f)
        && NearlyEquals(size.BorderThickness, 1.0f)
        && NearlyEquals(size.FontSize, 20.0f)
        && NearlyEquals(size.HorizontalPadding, 10.0f)
        && NearlyEquals(size.VerticalPadding, 6.0f)
        && NearlyEquals(size.CaretWidth, 2.25f);

    private static bool IsPreviousLightInputBackground(InputBoxColorOptions colors) =>
        IsColor(colors.Background, "38F5F5F5")
        && NearlyEquals(colors.BackgroundOpacity, 0.22f);

    private static bool IsLegacyDarkInputBackground(InputBoxColorOptions colors) =>
        IsColor(colors.Background, "80000000")
        && NearlyEquals(colors.BackgroundOpacity, 0.5f);

    private static bool IsLegacyDarkInputTheme(
        InputBoxColorOptions colors,
        InputBoxColorOptions defaults) =>
        IsLegacyDarkInputBackground(colors)
        && IsLegacyOrCurrentColor(colors.Border, "66FFFFFF", defaults.Border)
        && IsLegacyOrCurrentColor(colors.Text, "FFFFFFFF", defaults.Text)
        && NearlyEquals(colors.TextOpacity, 1.0f)
        && IsLegacyOrCurrentColor(colors.Caret, "FFFFFFFF", defaults.Caret);

    private static bool IsLegacyOrCurrentColor(
        string? color,
        string legacyArgb,
        string currentColor) =>
        IsColor(color, legacyArgb) || IsSameColor(color, currentColor);

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

    private static bool IsSameColor(string? left, string? right) =>
        string.Equals(
            left?.Trim().TrimStart('#'),
            right?.Trim().TrimStart('#'),
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
