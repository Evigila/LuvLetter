using System.Reflection;
using System.Text.Json;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Native;

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
                FeatureWindow = ActivationGestureKind.DoubleControlPress,
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
            FeatureWindow = new FeatureWindowConfiguration
            {
                Layout = new FeatureWindowLayoutOptions
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
                Colors = new FeatureWindowColorOptions
                {
                    BackgroundOpacity = float.PositiveInfinity,
                    TextOpacity = float.NaN,
                },
            },
        };

        var normalized = LuvLetterConfigurationStore.Normalize(invalid);
        var inputSize = normalized.InputBox.Size;
        var layout = normalized.FeatureWindow.Layout;

        Assert.Equal(
            FeatureWindowLayoutOptions.MaximumItemsPerPage,
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
            normalized.FeatureWindow.Colors.BackgroundOpacity,
            normalized.FeatureWindow.Colors.TextOpacity);

        Assert.Equal(32768, normalized.InputBox.Placement.OffsetX);
        Assert.Equal(-32768, normalized.InputBox.Placement.OffsetY);
        Assert.Equal(32768, layout.OffsetX);
        Assert.Equal(-32768, layout.OffsetY);

        Assert.NotEqual(
            normalized.ActivationGestures.InputBox,
            normalized.ActivationGestures.FeatureWindow,
            "The two Ctrl gestures must remain mutually exclusive.");
        Assert.Equal(
            ActivationGestureKind.ControlTapThenHold,
            normalized.ActivationGestures.FeatureWindow);
        Assert.True(normalized.ActivationGestures.AllowLeftControl);
        Assert.True(normalized.ActivationGestures.AllowRightControl);
        Assert.True(
            normalized.ActivationGestures.SecondPressTimeoutMs
                >= normalized.ActivationGestures.TapMaxDurationMs);
        Assert.True(
            normalized.ActivationGestures.HoldThresholdMs
                > normalized.ActivationGestures.TapMaxDurationMs);

        var numpadConflict = FeatureHotkeyRules.FindConflict(
            LuvLetterConfiguration.Default.FeatureWindow.Hotkeys with
            {
                PreviousPage = new HotkeyDefinition(
                    HotkeyModifierKeys.None,
                    0x62,
                    "NumPad2"),
                FirstItemVirtualKey = 0x32,
            },
            itemsPerPage: 7);
        Assert.Equal(
            FeatureHotkeyConflict.ItemActivationKey,
            numpadConflict,
            "Numpad aliases must obey the same feature activation conflict rules.");

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
            var changedCount = 0;
            store.Changed += static (_, _) => throw new InvalidOperationException("listener failure");
            store.Changed += (_, _) => changedCount++;

            var requested = LuvLetterConfiguration.Default with
            {
                InputBox = LuvLetterConfiguration.Default.InputBox with
                {
                    Size = LuvLetterConfiguration.Default.InputBox.Size with
                    {
                        Width = 777,
                    },
                },
                FeatureWindow = LuvLetterConfiguration.Default.FeatureWindow with
                {
                    Layout = LuvLetterConfiguration.Default.FeatureWindow.Layout with
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
            Assert.Equal(1, changedCount);
            using (JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                // Parsing proves the completed file contains a whole JSON document.
            }

            var secondSaved = store.Update(
                firstSaved with
                {
                    InputBox = firstSaved.InputBox with
                    {
                        Size = firstSaved.InputBox.Size with { Width = 888 },
                    },
                });
            Assert.Equal(2, changedCount);
            Assert.Equal(888, secondSaved.InputBox.Size.Width);

            var reloadedStore = new LuvLetterConfigurationStore(settingsPath);
            Assert.Equal(ConfigurationLoadStatus.Loaded, reloadedStore.InitialLoad.Status);
            var reloaded = reloadedStore.Current;
            Assert.Equal(LuvLetterConfiguration.CurrentSchemaVersion, reloaded.SchemaVersion);
            Assert.Equal(888, reloaded.InputBox.Size.Width);
            Assert.Equal(5, reloaded.FeatureWindow.Layout.ItemsPerPage);
            Assert.Equal(23, reloaded.FeatureWindow.Layout.OffsetX);
            Assert.Equal(-17, reloaded.FeatureWindow.Layout.OffsetY);
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
            Assert.Equal("#66FFFFFF", migratedVisual.InputBox.Colors.Border);
            Assert.Equal(1.0f, migratedVisual.InputBox.Colors.TextOpacity);
            Assert.Equal(1.0f, migratedVisual.FeatureWindow.Layout.BorderThickness);
            Assert.Equal(16.0f, migratedVisual.FeatureWindow.Layout.CornerRadius);
            Assert.Equal("#66FFFFFF", migratedVisual.FeatureWindow.Colors.Border);
            Assert.Equal(1.0f, migratedVisual.FeatureWindow.Colors.TextOpacity);

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
            Assert.Equal(12.0f, currentSchemaVisual.FeatureWindow.Layout.CornerRadius);
            Assert.Equal(2.0f, currentSchemaVisual.FeatureWindow.Layout.BorderThickness);
            Assert.Equal("#FFFFFFFF", currentSchemaVisual.FeatureWindow.Colors.Border);

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
            Assert.Equal("#80F5F5F5", migratedInputDefaults.InputBox.Colors.Background);
            Assert.Equal(0.5f, migratedInputDefaults.InputBox.Colors.BackgroundOpacity);

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
