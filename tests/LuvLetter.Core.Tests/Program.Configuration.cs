using System.Reflection;
using System.Text.Json;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestConfigurationNormalization()
    {
        var invalid = new LuvLetterConfiguration
        {
            ActivationGestures = new ActivationGestureOptions
            {
                InputBox = ActivationGestureKind.DoubleControlPress,
                QuickActions = ActivationGestureKind.DoubleControlPress,
                TapMaxDurationMs = -1,
                SecondPressTimeoutMs = -1,
                HoldThresholdMs = -1,
                AllowLeftControl = false,
                AllowRightControl = false,
            },
            InputBox = new InputBoxConfiguration
            {
                Placement = new InputBoxPlacementOptions
                {
                    OffsetX = int.MaxValue,
                    OffsetY = int.MinValue,
                },
                Size = new InputBoxSizeOptions
                {
                    CornerRadius = float.NaN,
                    BorderThickness = float.PositiveInfinity,
                    FontSize = float.NegativeInfinity,
                    HorizontalPadding = float.NaN,
                    VerticalPadding = float.PositiveInfinity,
                    CaretWidth = float.NegativeInfinity,
                },
                Colors = new InputBoxColorOptions
                {
                    BackgroundOpacity = float.NaN,
                    TextOpacity = float.PositiveInfinity,
                },
            },
            QuickActions = new QuickActionsConfiguration
            {
                Layout = new QuickActionsLayoutOptions
                {
                    ItemsPerPage = 99,
                    CellSize = float.NaN,
                    Gap = float.PositiveInfinity,
                    CornerRadius = float.NegativeInfinity,
                    BorderThickness = float.NaN,
                    FontSize = float.PositiveInfinity,
                    OffsetX = int.MaxValue,
                    OffsetY = int.MinValue,
                },
                Colors = new QuickActionsColorOptions
                {
                    BackgroundOpacity = float.PositiveInfinity,
                    TextOpacity = float.NaN,
                },
            },
        };

        var normalized = LuvLetterConfigurationStore.Normalize(invalid);
        var inputSize = normalized.InputBox.Size;
        var layout = normalized.QuickActions.Layout;

        Assert.Equal(
            QuickActionsLayoutOptions.MaximumItemsPerPage,
            layout.ItemsPerPage,
            "A page must never contain more than seven features.");
        Assert.AllFinite(
            inputSize.CornerRadius,
            inputSize.BorderThickness,
            inputSize.FontSize,
            inputSize.HorizontalPadding,
            inputSize.VerticalPadding,
            inputSize.CaretWidth,
            normalized.InputBox.Colors.BackgroundOpacity,
            normalized.InputBox.Colors.TextOpacity,
            layout.CellSize,
            layout.Gap,
            layout.CornerRadius,
            layout.BorderThickness,
            layout.FontSize,
            normalized.QuickActions.Colors.BackgroundOpacity,
            normalized.QuickActions.Colors.TextOpacity);

        Assert.Equal(32768, normalized.InputBox.Placement.OffsetX);
        Assert.Equal(-32768, normalized.InputBox.Placement.OffsetY);
        Assert.Equal(32768, layout.OffsetX);
        Assert.Equal(-32768, layout.OffsetY);

        Assert.NotEqual(
            normalized.ActivationGestures.InputBox,
            normalized.ActivationGestures.QuickActions,
            "The legacy serialized gesture fields must retain their canonical values.");
        Assert.Equal(
            ActivationGestureKind.ControlTapThenHold,
            normalized.ActivationGestures.QuickActions);
        Assert.True(normalized.ActivationGestures.AllowLeftControl);
        Assert.True(normalized.ActivationGestures.AllowRightControl);
        Assert.True(
            normalized.ActivationGestures.SecondPressTimeoutMs
                >= normalized.ActivationGestures.TapMaxDurationMs);
        Assert.True(
            normalized.ActivationGestures.HoldThresholdMs
                > normalized.ActivationGestures.TapMaxDurationMs);

        var numpadConflict = QuickActionHotkeyRules.FindConflict(
            LuvLetterConfiguration.Default.QuickActions.Hotkeys with
            {
                PreviousPage = new HotkeyDefinition(
                    HotkeyModifierKeys.None,
                    0x62,
                    "NumPad2"),
                FirstItemVirtualKey = 0x32,
            },
            itemsPerPage: 7);
        Assert.Equal(
            QuickActionHotkeyConflict.ItemActivationKey,
            numpadConflict,
            "Numpad aliases must obey the same Quick Action activation conflict rules.");

        var independentTextOpacity = LuvLetterConfigurationStore.Normalize(
            LuvLetterConfiguration.Default with
            {
                InputBox = LuvLetterConfiguration.Default.InputBox with
                {
                    Colors = LuvLetterConfiguration.Default.InputBox.Colors with
                    {
                        BackgroundOpacity = 0.05f,
                        Text = "#80FFFFFF",
                        TextOpacity = 0.75f,
                    },
                },
            });
        Assert.Equal("#80FFFFFF", independentTextOpacity.InputBox.Colors.Text);
        Assert.Equal(0.75f, independentTextOpacity.InputBox.Colors.TextOpacity);

        var multiplyOpacity = typeof(NativeConfigurationMapper).GetMethod(
            "MultiplyOpacity",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var multipliedTextColor = (uint)multiplyOpacity.Invoke(
            null,
            [0x80FFFFFFu, 0.75f])!;
        Assert.Equal(
            0x60FFFFFFu,
            multipliedTextColor,
            "Text opacity must multiply the text alpha independently of background opacity.");

        return Task.CompletedTask;
    }

    private static Task TestConfigurationStore()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "LuvLetter.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
            var store = new LuvLetterConfigurationStore(settingsPath);
            Assert.Equal(ConfigurationLoadStatus.NotFound, store.InitialLoad.Status);

            var requested = LuvLetterConfiguration.Default with
            {
                InputBox = LuvLetterConfiguration.Default.InputBox with
                {
                    Size = LuvLetterConfiguration.Default.InputBox.Size with
                    {
                        Width = 777,
                    },
                },
                QuickActions = LuvLetterConfiguration.Default.QuickActions with
                {
                    Layout = LuvLetterConfiguration.Default.QuickActions.Layout with
                    {
                        ItemsPerPage = 5,
                        OffsetX = 23,
                        OffsetY = -17,
                    },
                },
            };

            var firstSaved = store.Update(requested);
            Assert.True(File.Exists(settingsPath));
            Assert.True(ReferenceEquals(firstSaved, store.Current));
            using (var savedDocument = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                Assert.True(savedDocument.RootElement.TryGetProperty("QuickActions", out _));
                Assert.False(savedDocument.RootElement.TryGetProperty("FeatureWindow", out _));
            }

            var secondSaved = store.Update(
                firstSaved with
                {
                    InputBox = firstSaved.InputBox with
                    {
                        Size = firstSaved.InputBox.Size with { Width = 888 },
                    },
                });
            Assert.Equal(888, secondSaved.InputBox.Size.Width);

            var reloadedStore = new LuvLetterConfigurationStore(settingsPath);
            Assert.Equal(ConfigurationLoadStatus.Loaded, reloadedStore.InitialLoad.Status);
            var reloaded = reloadedStore.Current;
            Assert.Equal(LuvLetterConfiguration.CurrentSchemaVersion, reloaded.SchemaVersion);
            Assert.Equal(888, reloaded.InputBox.Size.Width);
            Assert.Equal(5, reloaded.QuickActions.Layout.ItemsPerPage);
            Assert.Equal(23, reloaded.QuickActions.Layout.OffsetX);
            Assert.Equal(-17, reloaded.QuickActions.Layout.OffsetY);
            Assert.Empty(
                Directory.EnumerateFiles(temporaryDirectory, "*.tmp"),
                "Atomic persistence left a temporary file behind.");

            var legacyPath = Path.Combine(temporaryDirectory, "legacy-hotkey.json");
            File.WriteAllText(
                legacyPath,
                """
                {
                  "Modifiers": 2,
                  "VirtualKey": 75,
                  "KeyName": "K",
                }
                """);

            var legacyStore = new LuvLetterConfigurationStore(legacyPath);
            Assert.Equal(ConfigurationLoadStatus.Invalid, legacyStore.InitialLoad.Status);
            var migrated = legacyStore.Current;
            Assert.Equal(
                ActivationGestureKind.DoubleControlPress,
                migrated.ActivationGestures.InputBox);

            var invalidPath = Path.Combine(temporaryDirectory, "invalid.json");
            File.WriteAllText(invalidPath, "{ definitely-not-json }");
            var invalidStore = new LuvLetterConfigurationStore(invalidPath);
            Assert.Equal(ConfigurationLoadStatus.Invalid, invalidStore.InitialLoad.Status);
            Assert.True(invalidStore.InitialLoad.HasWarning);
            Assert.Equal(
                LuvLetterConfiguration.Default.InputBox.Size.Width,
                invalidStore.Current.InputBox.Size.Width);

            var unreadablePath = Path.Combine(temporaryDirectory, "settings-directory");
            Directory.CreateDirectory(unreadablePath);
            var unreadableStore = new LuvLetterConfigurationStore(unreadablePath);
            Assert.Equal(
                ConfigurationLoadStatus.IoFailure,
                unreadableStore.InitialLoad.Status);

            var futurePath = Path.Combine(temporaryDirectory, "future.json");
            File.WriteAllText(
                futurePath,
                $$"""
                {
                  "SchemaVersion": {{LuvLetterConfiguration.CurrentSchemaVersion + 1}},
                  "InputBox": { "Size": { "Width": 777 } }
                }
                """);
            var futureStore = new LuvLetterConfigurationStore(futurePath);
            Assert.Equal(
                ConfigurationLoadStatus.UnsupportedVersion,
                futureStore.InitialLoad.Status);
            Assert.Equal(
                LuvLetterConfiguration.Default.InputBox.Size.Width,
                futureStore.Current.InputBox.Size.Width);

            var legacyVisualPath = Path.Combine(temporaryDirectory, "legacy-visual-v2.json");
            File.WriteAllText(
                legacyVisualPath,
                """
                {
                  "SchemaVersion": 2,
                  "InputBox": {
                    "Size": { "CornerRadius": 8, "BorderThickness": 2 },
                    "Colors": { "Border": "#FFFFFFFF" }
                  },
                  "ActivationGestures": {
                    "InputBox": 0,
                    "FeatureWindow": 1
                  },
                  "FeatureWindow": {
                    "Layout": { "CornerRadius": 12, "BorderThickness": 2 },
                    "Colors": { "Border": "FFFFFF" }
                  }
                }
                """);

            var migratedVisual = new LuvLetterConfigurationStore(legacyVisualPath).Current;
            Assert.Equal(LuvLetterConfiguration.CurrentSchemaVersion, migratedVisual.SchemaVersion);
            Assert.Equal(1.0f, migratedVisual.InputBox.Size.BorderThickness);
            Assert.Equal(8.0f, migratedVisual.InputBox.Size.CornerRadius);
            Assert.Equal(SurfaceStyleDefaults.Border, migratedVisual.InputBox.Colors.Border);
            Assert.Equal(1.0f, migratedVisual.InputBox.Colors.TextOpacity);
            Assert.Equal(1.0f, migratedVisual.QuickActions.Layout.BorderThickness);
            Assert.Equal(8.0f, migratedVisual.QuickActions.Layout.CornerRadius);
            Assert.Equal(SurfaceStyleDefaults.Border, migratedVisual.QuickActions.Colors.Border);
            Assert.Equal(1.0f, migratedVisual.QuickActions.Colors.TextOpacity);
            Assert.Equal(
                ActivationGestureKind.ControlTapThenHold,
                migratedVisual.ActivationGestures.QuickActions);

            var customizedVisualPath = Path.Combine(temporaryDirectory, "customized-visual-v2.json");
            File.WriteAllText(
                customizedVisualPath,
                """
                {
                  "SchemaVersion": 2,
                  "InputBox": {
                    "Size": { "CornerRadius": 9, "BorderThickness": 2 },
                    "Colors": { "Border": "#FFFFFFFF" }
                  }
                }
                """);
            var customizedVisual = new LuvLetterConfigurationStore(customizedVisualPath).Current;
            Assert.Equal(9.0f, customizedVisual.InputBox.Size.CornerRadius);
            Assert.Equal(2.0f, customizedVisual.InputBox.Size.BorderThickness);
            Assert.Equal("#FFFFFFFF", customizedVisual.InputBox.Colors.Border);

            var currentSchemaVisualPath = Path.Combine(temporaryDirectory, "current-visual-v3.json");
            File.WriteAllText(
                currentSchemaVisualPath,
                """
                {
                  "SchemaVersion": 3,
                  "FeatureWindow": {
                    "Layout": { "CornerRadius": 12, "BorderThickness": 2 },
                    "Colors": { "Border": "#FFFFFFFF" }
                  }
                }
                """);
            var currentSchemaVisual = new LuvLetterConfigurationStore(currentSchemaVisualPath).Current;
            Assert.Equal(12.0f, currentSchemaVisual.QuickActions.Layout.CornerRadius);
            Assert.Equal(2.0f, currentSchemaVisual.QuickActions.Layout.BorderThickness);
            Assert.Equal("#FFFFFFFF", currentSchemaVisual.QuickActions.Colors.Border);

            var conflictingNamesPath = Path.Combine(
                temporaryDirectory,
                "conflicting-quick-actions-v5.json");
            File.WriteAllText(
                conflictingNamesPath,
                """
                {
                  "SchemaVersion": 5,
                  "QuickActions": {},
                  "FeatureWindow": {}
                }
                """);
            var conflictingNames = new LuvLetterConfigurationStore(conflictingNamesPath);
            Assert.Equal(ConfigurationLoadStatus.Invalid, conflictingNames.InitialLoad.Status);

            var duplicateCanonicalPath = Path.Combine(
                temporaryDirectory,
                "duplicate-quick-actions-v6.json");
            File.WriteAllText(
                duplicateCanonicalPath,
                """
                {
                  "SchemaVersion": 6,
                  "QuickActions": {},
                  "QuickActions": {}
                }
                """);
            var duplicateCanonical = new LuvLetterConfigurationStore(duplicateCanonicalPath);
            Assert.Equal(ConfigurationLoadStatus.Invalid, duplicateCanonical.InitialLoad.Status);

            var previousInputDefaultsPath = Path.Combine(
                temporaryDirectory,
                "previous-input-defaults-v3.json");
            File.WriteAllText(
                previousInputDefaultsPath,
                """
                {
                  "SchemaVersion": 3,
                  "InputBox": {
                    "Size": {
                      "Width": 640,
                      "Height": 44,
                      "CornerRadius": 10,
                      "BorderThickness": 1,
                      "FontSize": 20,
                      "HorizontalPadding": 10,
                      "VerticalPadding": 6,
                      "CaretWidth": 2.25
                    },
                    "Colors": {
                      "Background": "#38F5F5F5",
                      "BackgroundOpacity": 0.22
                    }
                  }
                }
                """);
            var migratedInputDefaults = new LuvLetterConfigurationStore(
                previousInputDefaultsPath).Current;
            Assert.Equal(560, migratedInputDefaults.InputBox.Size.Width);
            Assert.Equal(32, migratedInputDefaults.InputBox.Size.Height);
            Assert.Equal(8.0f, migratedInputDefaults.InputBox.Size.CornerRadius);
            Assert.Equal(14.0f, migratedInputDefaults.InputBox.Size.FontSize);
            Assert.Equal(4.0f, migratedInputDefaults.InputBox.Size.VerticalPadding);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedInputDefaults.InputBox.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.BackgroundOpacity,
                migratedInputDefaults.InputBox.Colors.BackgroundOpacity);

            var legacyDarkInputPath = Path.Combine(
                temporaryDirectory,
                "legacy-dark-input-v3.json");
            File.WriteAllText(
                legacyDarkInputPath,
                """
                {
                  "SchemaVersion": 3,
                  "InputBox": {
                    "Size": {
                      "Width": 640,
                      "Height": 44,
                      "CornerRadius": 10,
                      "BorderThickness": 1,
                      "FontSize": 20,
                      "HorizontalPadding": 10,
                      "VerticalPadding": 6,
                      "CaretWidth": 2.25
                    },
                    "Colors": {
                      "Border": "#66FFFFFF",
                      "Background": "#80000000",
                      "BackgroundOpacity": 0.5,
                      "Text": "#FFFFFFFF",
                      "TextOpacity": 1,
                      "Caret": "#FFFFFFFF"
                    }
                  }
                }
                """);
            var migratedLegacyDarkInput = new LuvLetterConfigurationStore(
                legacyDarkInputPath).Current;
            Assert.Equal(640, migratedLegacyDarkInput.InputBox.Size.Width);
            Assert.Equal(44, migratedLegacyDarkInput.InputBox.Size.Height);
            Assert.Equal(
                SurfaceStyleDefaults.CornerRadius,
                migratedLegacyDarkInput.InputBox.Size.CornerRadius);
            Assert.Equal(
                SurfaceStyleDefaults.Border,
                migratedLegacyDarkInput.InputBox.Colors.Border);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedLegacyDarkInput.InputBox.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedLegacyDarkInput.InputBox.Colors.Text);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedLegacyDarkInput.InputBox.Colors.Caret);

            var persistedHybridInputPath = Path.Combine(
                temporaryDirectory,
                "persisted-hybrid-input-v8.json");
            File.WriteAllText(
                persistedHybridInputPath,
                """
                {
                  "SchemaVersion": 8,
                  "InputBox": {
                    "Size": {
                      "Width": 640,
                      "Height": 44,
                      "CornerRadius": 10,
                      "BorderThickness": 1,
                      "FontSize": 20,
                      "HorizontalPadding": 10,
                      "VerticalPadding": 6,
                      "CaretWidth": 2.25
                    },
                    "Colors": {
                      "Border": "#FFFFFFFF",
                      "Background": "#80000000",
                      "BackgroundOpacity": 0.5,
                      "Text": "#FF3F3F3F",
                      "TextOpacity": 1,
                      "Caret": "#FF3F3F3F"
                    }
                  }
                }
                """);
            var migratedHybridInput = new LuvLetterConfigurationStore(
                persistedHybridInputPath).Current;
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedHybridInput.InputBox.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedHybridInput.InputBox.Colors.Text);

            var customizedDarkInputPath = Path.Combine(
                temporaryDirectory,
                "customized-dark-input-v8.json");
            File.WriteAllText(
                customizedDarkInputPath,
                """
                {
                  "SchemaVersion": 8,
                  "InputBox": {
                    "Size": {
                      "Width": 639,
                      "Height": 44,
                      "CornerRadius": 10,
                      "BorderThickness": 1,
                      "FontSize": 20,
                      "HorizontalPadding": 10,
                      "VerticalPadding": 6,
                      "CaretWidth": 2.25
                    },
                    "Colors": {
                      "Border": "#66FFFFFF",
                      "Background": "#80000000",
                      "BackgroundOpacity": 0.5,
                      "Text": "#FFFFFFFF",
                      "TextOpacity": 1,
                      "Caret": "#FFFFFFFF"
                    }
                  }
                }
                """);
            var customizedDarkInput = new LuvLetterConfigurationStore(
                customizedDarkInputPath).Current;
            Assert.Equal("#80000000", customizedDarkInput.InputBox.Colors.Background);
            Assert.Equal("#FFFFFFFF", customizedDarkInput.InputBox.Colors.Text);

            var customizedPreviousInputPath = Path.Combine(
                temporaryDirectory,
                "customized-previous-input-v3.json");
            File.WriteAllText(
                customizedPreviousInputPath,
                """
                {
                  "SchemaVersion": 3,
                  "InputBox": {
                    "Size": {
                      "Width": 639,
                      "Height": 44,
                      "CornerRadius": 10,
                      "BorderThickness": 1,
                      "FontSize": 20,
                      "HorizontalPadding": 10,
                      "VerticalPadding": 6,
                      "CaretWidth": 2.25
                    },
                    "Colors": {
                      "Background": "#38F5F5F5",
                      "BackgroundOpacity": 0.22
                    }
                  }
                }
                """);
            var customizedPreviousInput = new LuvLetterConfigurationStore(
                customizedPreviousInputPath).Current;
            Assert.Equal(639, customizedPreviousInput.InputBox.Size.Width);
            Assert.Equal(44, customizedPreviousInput.InputBox.Size.Height);
            Assert.Equal(20.0f, customizedPreviousInput.InputBox.Size.FontSize);
            Assert.Equal("#38F5F5F5", customizedPreviousInput.InputBox.Colors.Background);
            Assert.Equal(0.22f, customizedPreviousInput.InputBox.Colors.BackgroundOpacity);

            var previousQuickActionsDefaultsPath = Path.Combine(
                temporaryDirectory,
                "previous-quick-actions-defaults-v6.json");
            File.WriteAllText(
                previousQuickActionsDefaultsPath,
                """
                {
                  "SchemaVersion": 6,
                  "QuickActions": {
                    "Layout": { "CornerRadius": 16 },
                    "Colors": {
                      "Background": "#38F5F5F5",
                      "BackgroundOpacity": 0.22
                    }
                  }
                }
                """);
            var migratedQuickActionsDefaults = new LuvLetterConfigurationStore(
                previousQuickActionsDefaultsPath).Current;
            Assert.Equal(8.0f, migratedQuickActionsDefaults.QuickActions.Layout.CornerRadius);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedQuickActionsDefaults.QuickActions.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.BackgroundOpacity,
                migratedQuickActionsDefaults.QuickActions.Colors.BackgroundOpacity);

            var customizedQuickActionsPath = Path.Combine(
                temporaryDirectory,
                "customized-quick-actions-v6.json");
            File.WriteAllText(
                customizedQuickActionsPath,
                """
                {
                  "SchemaVersion": 6,
                  "QuickActions": {
                    "Layout": { "CornerRadius": 11 },
                    "Colors": {
                      "Background": "#66445566",
                      "BackgroundOpacity": 0.4
                    }
                  }
                }
                """);
            var customizedQuickActions = new LuvLetterConfigurationStore(
                customizedQuickActionsPath).Current;
            Assert.Equal(11.0f, customizedQuickActions.QuickActions.Layout.CornerRadius);
            Assert.Equal("#66445566", customizedQuickActions.QuickActions.Colors.Background);
            Assert.Equal(0.4f, customizedQuickActions.QuickActions.Colors.BackgroundOpacity);

            var previousUnifiedThemePath = Path.Combine(
                temporaryDirectory,
                "previous-unified-theme-v7.json");
            File.WriteAllText(
                previousUnifiedThemePath,
                """
                {
                  "SchemaVersion": 7,
                  "InputBox": {
                    "Colors": {
                      "Border": "#66FFFFFF",
                      "Background": "#80F5F5F5",
                      "BackgroundOpacity": 0.5,
                      "Text": "#FFFFFFFF",
                      "TextOpacity": 1,
                      "Caret": "#FFFFFFFF"
                    }
                  },
                  "QuickActions": {
                    "Colors": {
                      "Border": "#66FFFFFF",
                      "Background": "#80F5F5F5",
                      "BackgroundOpacity": 0.5,
                      "Text": "#FFFFFFFF",
                      "TextOpacity": 1,
                      "Accent": "#FFFFFFFF"
                    }
                  }
                }
                """);
            var migratedUnifiedTheme = new LuvLetterConfigurationStore(
                previousUnifiedThemePath).Current;
            Assert.Equal(
                SurfaceStyleDefaults.Border,
                migratedUnifiedTheme.InputBox.Colors.Border);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedUnifiedTheme.InputBox.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedUnifiedTheme.InputBox.Colors.Text);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedUnifiedTheme.InputBox.Colors.Caret);
            Assert.Equal(
                SurfaceStyleDefaults.Border,
                migratedUnifiedTheme.QuickActions.Colors.Border);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedUnifiedTheme.QuickActions.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedUnifiedTheme.QuickActions.Colors.Text);
            Assert.Equal(
                SurfaceStyleDefaults.Content,
                migratedUnifiedTheme.QuickActions.Colors.Accent);

            var customizedUnifiedThemePath = Path.Combine(
                temporaryDirectory,
                "customized-unified-theme-v7.json");
            File.WriteAllText(
                customizedUnifiedThemePath,
                """
                {
                  "SchemaVersion": 7,
                  "InputBox": {
                    "Colors": {
                      "Border": "#FF55AAFF",
                      "Background": "#CC223344",
                      "BackgroundOpacity": 0.8,
                      "Text": "#FF112233",
                      "TextOpacity": 0.75,
                      "Caret": "#FFFFAA00"
                    }
                  },
                  "QuickActions": {
                    "Colors": {
                      "Border": "#FF8844CC",
                      "Background": "#99334455",
                      "BackgroundOpacity": 0.6,
                      "Text": "#FF223344",
                      "TextOpacity": 0.7,
                      "Accent": "#FF00AA88"
                    }
                  }
                }
                """);
            var customizedUnifiedTheme = new LuvLetterConfigurationStore(
                customizedUnifiedThemePath).Current;
            Assert.Equal("#FF55AAFF", customizedUnifiedTheme.InputBox.Colors.Border);
            Assert.Equal("#CC223344", customizedUnifiedTheme.InputBox.Colors.Background);
            Assert.Equal(0.8f, customizedUnifiedTheme.InputBox.Colors.BackgroundOpacity);
            Assert.Equal("#FF112233", customizedUnifiedTheme.InputBox.Colors.Text);
            Assert.Equal(0.75f, customizedUnifiedTheme.InputBox.Colors.TextOpacity);
            Assert.Equal("#FFFFAA00", customizedUnifiedTheme.InputBox.Colors.Caret);
            Assert.Equal("#FF8844CC", customizedUnifiedTheme.QuickActions.Colors.Border);
            Assert.Equal("#99334455", customizedUnifiedTheme.QuickActions.Colors.Background);
            Assert.Equal(0.6f, customizedUnifiedTheme.QuickActions.Colors.BackgroundOpacity);
            Assert.Equal("#FF223344", customizedUnifiedTheme.QuickActions.Colors.Text);
            Assert.Equal(0.7f, customizedUnifiedTheme.QuickActions.Colors.TextOpacity);
            Assert.Equal("#FF00AA88", customizedUnifiedTheme.QuickActions.Colors.Accent);

            var previousSilverThemePath = Path.Combine(
                temporaryDirectory,
                "previous-silver-theme-v9.json");
            File.WriteAllText(
                previousSilverThemePath,
                """
                {
                  "SchemaVersion": 9,
                  "InputBox": {
                    "Colors": {
                      "Background": "#C0C0C0",
                      "BackgroundOpacity": 1
                    }
                  },
                  "QuickActions": {
                    "Colors": {
                      "Background": "#FFC0C0C0",
                      "BackgroundOpacity": 1
                    }
                  }
                }
                """);
            var migratedSilverTheme = new LuvLetterConfigurationStore(
                previousSilverThemePath).Current;
            Assert.Equal(
                LuvLetterConfiguration.CurrentSchemaVersion,
                migratedSilverTheme.SchemaVersion);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedSilverTheme.InputBox.Colors.Background);
            Assert.Equal(
                SurfaceStyleDefaults.Background,
                migratedSilverTheme.QuickActions.Colors.Background);

            var customizedSilverThemePath = Path.Combine(
                temporaryDirectory,
                "customized-silver-theme-v9.json");
            File.WriteAllText(
                customizedSilverThemePath,
                """
                {
                  "SchemaVersion": 9,
                  "InputBox": {
                    "Colors": {
                      "Background": "#FFC0C0C0",
                      "BackgroundOpacity": 0.5
                    }
                  },
                  "QuickActions": {
                    "Colors": {
                      "Background": "#FF223344",
                      "BackgroundOpacity": 1
                    }
                  }
                }
                """);
            var customizedSilverTheme = new LuvLetterConfigurationStore(
                customizedSilverThemePath).Current;
            Assert.Equal("#80C0C0C0", customizedSilverTheme.InputBox.Colors.Background);
            Assert.Equal(0.5f, customizedSilverTheme.InputBox.Colors.BackgroundOpacity);
            Assert.Equal("#FF223344", customizedSilverTheme.QuickActions.Colors.Background);
            Assert.Equal(1.0f, customizedSilverTheme.QuickActions.Colors.BackgroundOpacity);

            TestFailedSaveDoesNotChangeCurrent(temporaryDirectory);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void TestFailedSaveDoesNotChangeCurrent(string temporaryDirectory)
    {
        var pathBlocker = Path.Combine(temporaryDirectory, "not-a-directory");
        File.WriteAllText(pathBlocker, "block directory creation");

        var store = new LuvLetterConfigurationStore(Path.Combine(pathBlocker, "settings.json"));
        var before = store.Current;
        Assert.Throws<Exception>(
            () => store.Update(
                before with
                {
                    InputBox = before.InputBox with
                    {
                        Size = before.InputBox.Size with { Width = 999 },
                    },
                }));
        Assert.True(
            ReferenceEquals(before, store.Current),
            "A failed atomic save changed the in-memory configuration.");
    }
}
