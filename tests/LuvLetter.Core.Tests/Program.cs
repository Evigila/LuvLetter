using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Hotkeys;

namespace LuvLetter.Core.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("Default activation gestures", TestDefaultActivationGestures),
        ("Ctrl gesture default double tap", TestCtrlGestureDefaultDoubleTap),
        ("Ctrl gesture default tap then hold", TestCtrlGestureDefaultTapThenHold),
        ("Ctrl gesture cancellation boundaries", TestCtrlGestureCancellationBoundaries),
        ("Ctrl gesture side and auto-repeat handling", TestCtrlGestureSidesAndAutoRepeat),
        ("Ctrl gesture configurable mapping", TestCtrlGestureConfigurableMapping),
        ("Configuration normalization", TestConfigurationNormalization),
        ("Feature registry", TestFeatureRegistry),
        ("Asynchronous command dispatch", TestCommandDispatcher),
        ("Managed native ABI layout", TestManagedNativeAbiLayout),
        ("Configuration store round-trip and migration", TestConfigurationStore),
    ];

    public static async Task<int> Main()
    {
        var passed = 0;
        foreach (var test in Tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                passed++;
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL  {test.Name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{passed}/{Tests.Length} smoke tests passed.");
        return passed == Tests.Length ? 0 : 1;
    }

    private static Task TestDefaultActivationGestures()
    {
        var defaults = LuvLetterConfiguration.Default.ActivationGestures;

        Assert.Equal(
            ActivationGestureKind.DoubleControlPress,
            defaults.InputBox,
            "The command input box must default to double Ctrl.");
        Assert.Equal(
            ActivationGestureKind.ControlTapThenHold,
            defaults.FeatureWindow,
            "The feature window must default to tap Ctrl, then hold Ctrl.");
        Assert.True(defaults.AllowLeftControl, "Left Ctrl should be enabled by default.");
        Assert.True(defaults.AllowRightControl, "Right Ctrl should be enabled by default.");

        var configuration = LuvLetterConfiguration.Default;
        Assert.Equal(1.0f, configuration.InputBox.Size.BorderThickness);
        Assert.Equal(10.0f, configuration.InputBox.Size.CornerRadius);
        Assert.Equal("#66FFFFFF", configuration.InputBox.Colors.Border);
        Assert.Equal(1.0f, configuration.FeatureWindow.Layout.BorderThickness);
        Assert.Equal(16.0f, configuration.FeatureWindow.Layout.CornerRadius);
        Assert.Equal("#66FFFFFF", configuration.FeatureWindow.Colors.Border);

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureDefaultDoubleTap()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);
        const long firstPressAt = 1_000;
        var firstReleaseAt = firstPressAt + options.TapMaxDurationMs;
        var secondPressAt = firstReleaseAt + options.SecondPressTimeoutMs;
        var secondReleaseAt = secondPressAt + options.TapMaxDurationMs;

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, firstPressAt));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, firstReleaseAt),
            "A tap exactly at the configured maximum duration must remain valid.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, secondPressAt),
            "The second press exactly at its configured timeout must remain valid.");
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleControlUp(ControlKeySide.Left, secondReleaseAt),
            "The default double-Ctrl gesture must request the command input box.");

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(10_000),
            "A completed gesture must not fire again from a stale timer callback.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureDefaultTapThenHold()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);

        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Right, 2_000));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 2_040));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Right, 2_150));

        var holdDeadline = machine.NextDeadlineTimestampMs;
        Assert.True(holdDeadline.HasValue, "The second press must arm the hold deadline.");
        Assert.Equal(
            2_150L + options.HoldThresholdMs,
            holdDeadline!.Value,
            "The hold deadline must use the configured threshold.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(holdDeadline.Value - 1),
            "Holding just below the threshold must not activate a feature.");
        Assert.Equal(
            CtrlGestureAction.FeatureWindowRequested,
            machine.HandleTimeout(holdDeadline.Value),
            "The default tap-then-hold gesture must request the feature window at the threshold.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(holdDeadline.Value + 1_000),
            "The hold gesture must fire only once.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, holdDeadline.Value + 1_001));

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureCancellationBoundaries()
    {
        var options = LuvLetterConfiguration.Default.ActivationGestures;
        var machine = new CtrlGestureStateMachine(options);

        machine.HandleControlDown(ControlKeySide.Left, 3_000);
        machine.HandleControlUp(ControlKeySide.Left, 3_020);
        var secondPressDeadline = machine.NextDeadlineTimestampMs;
        Assert.True(secondPressDeadline.HasValue, "The first tap must arm the second-press deadline.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleTimeout(secondPressDeadline!.Value),
            "Waiting too long for the second press must cancel the sequence.");
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, secondPressDeadline.Value + 1));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, secondPressDeadline.Value + 20),
            "A Ctrl press after the timeout must start a new sequence, not complete the old one.");

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 4_000);
        machine.HandleControlUp(ControlKeySide.Left, 4_020);
        machine.HandleOtherKey(4_030);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 4_040));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, 4_060),
            "Another key between Ctrl presses must cancel the pending gesture.");

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 5_000);
        machine.HandleOtherKey(5_010);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Left, 5_020),
            "Another key while Ctrl is down must suppress the gesture until Ctrl is released.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureSidesAndAutoRepeat()
    {
        var leftOnly = LuvLetterConfiguration.Default.ActivationGestures with
        {
            AllowLeftControl = true,
            AllowRightControl = false,
        };
        var machine = new CtrlGestureStateMachine(leftOnly);

        Assert.Equal(CtrlGestureAction.None, machine.HandleControlDown(ControlKeySide.Right, 6_000));
        Assert.Equal(CtrlGestureAction.None, machine.HandleControlUp(ControlKeySide.Right, 6_010));
        Assert.Equal(CtrlGestureAction.None, machine.HandleControlDown(ControlKeySide.Right, 6_020));
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 6_030),
            "A disabled Ctrl side must never activate a gesture.");

        machine.HandleControlDown(ControlKeySide.Left, 6_100);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 6_110),
            "Auto-repeat during the first press must not count as another physical press.");
        machine.HandleControlUp(ControlKeySide.Left, 6_120);
        machine.HandleControlDown(ControlKeySide.Left, 6_150);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlDown(ControlKeySide.Left, 6_160),
            "Auto-repeat during the second press must not trigger early.");
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleControlUp(ControlKeySide.Left, 6_180));

        machine.Reset();
        machine.HandleControlDown(ControlKeySide.Left, 6_300);
        machine.HandleControlUp(ControlKeySide.Left, 6_320);
        machine.HandleControlDown(ControlKeySide.Right, 6_350);
        Assert.Equal(
            CtrlGestureAction.None,
            machine.HandleControlUp(ControlKeySide.Right, 6_370),
            "A gesture must not mix left and right Ctrl presses.");

        return Task.CompletedTask;
    }

    private static Task TestCtrlGestureConfigurableMapping()
    {
        var swapped = LuvLetterConfiguration.Default.ActivationGestures with
        {
            InputBox = ActivationGestureKind.ControlTapThenHold,
            FeatureWindow = ActivationGestureKind.DoubleControlPress,
        };
        var machine = new CtrlGestureStateMachine(swapped);

        machine.HandleControlDown(ControlKeySide.Left, 7_000);
        machine.HandleControlUp(ControlKeySide.Left, 7_020);
        machine.HandleControlDown(ControlKeySide.Left, 7_100);
        Assert.Equal(
            CtrlGestureAction.FeatureWindowRequested,
            machine.HandleControlUp(ControlKeySide.Left, 7_120),
            "Swapping the mapping must make double Ctrl open the feature window.");

        machine.HandleControlDown(ControlKeySide.Left, 8_000);
        machine.HandleControlUp(ControlKeySide.Left, 8_020);
        machine.HandleControlDown(ControlKeySide.Left, 8_100);
        Assert.Equal(
            CtrlGestureAction.CommandRequested,
            machine.HandleTimeout(8_100 + swapped.HoldThresholdMs),
            "Swapping the mapping must make tap-then-hold open the command input box.");

        return Task.CompletedTask;
    }

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
            layout.CellSize,
            layout.Gap,
            layout.CornerRadius,
            layout.BorderThickness,
            layout.FontSize,
            normalized.FeatureWindow.Colors.BackgroundOpacity);

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

        return Task.CompletedTask;
    }

    private static Task TestFeatureRegistry()
    {
        var registry = new FeatureRegistry();
        var changedCount = 0;
        var activations = new List<int>();

        // A failing plug-in listener must not prevent later listeners from observing changes.
        registry.Changed += static (_, _) => throw new InvalidOperationException("listener failure");
        registry.Changed += (_, _) => changedCount++;

        for (var number = 1; number <= 9; number++)
        {
            var capturedNumber = number;
            var added = registry.Register(
                new FeatureDefinition(
                    $"test-{number}",
                    $"Test feature {number}",
                    () => activations.Add(capturedNumber)));
            Assert.True(added, $"Feature {number} was not registered.");
        }

        Assert.Equal(9, changedCount);
        Assert.SequenceEqual(
            Enumerable.Range(1, 9).Select(number => $"test-{number}"),
            registry.Snapshot().Select(feature => feature.Id),
            "Registration order changed.");

        Assert.False(
            registry.Register(
                new FeatureDefinition("test-4", "Rejected duplicate", static () => { })));
        Assert.Equal(9, changedCount, "A rejected duplicate must not publish Changed.");

        Assert.True(
            registry.Register(
                new FeatureDefinition(
                    "test-4",
                    "Replacement feature 4",
                    () => activations.Add(40)),
                FeatureRegistrationMode.ReplaceExisting));
        Assert.Equal(10, changedCount);
        Assert.Equal("test-4", registry.Snapshot()[3].Id);
        Assert.Equal("Replacement feature 4", registry.Snapshot()[3].DisplayName);

        Assert.True(registry.TryActivate(3));
        Assert.True(registry.TryActivate("test-1"));
        Assert.SequenceEqual([40, 1], activations);
        Assert.False(registry.TryActivate("missing"));
        Assert.False(registry.TryActivate(99));

        Assert.True(
            registry.Register(
                new FeatureDefinition(
                    "test-5",
                    "Throwing feature 5",
                    static () => throw new InvalidOperationException("feature failure")),
                FeatureRegistrationMode.ReplaceExisting));
        Assert.False(registry.TryActivate("test-5"), "Feature exceptions must be isolated.");
        Assert.True(registry.TryActivate("test-6"), "A failed feature must not poison the registry.");
        Assert.SequenceEqual([40, 1, 6], activations);

        Assert.True(registry.Unregister("test-2"));
        Assert.True(
            registry.Register(
                new FeatureDefinition("test-10", "Dynamic feature 10", static () => { })));
        Assert.Equal(13, changedCount, "Dynamic replacement/removal/addition must publish Changed.");
        Assert.Equal(9, registry.Snapshot().Count);
        Assert.Equal("test-10", registry.Snapshot()[^1].Id);

        return Task.CompletedTask;
    }

    private static Task TestCommandDispatcher()
    {
        using var dispatcher = new CommandDispatcher(capacity: 2);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackCompleted = new ManualResetEventSlim();

        CommandInvocation? invocation = null;
        var callbackThreadId = 0;
        var callerThreadId = Environment.CurrentManagedThreadId;

        Assert.True(
            dispatcher.Register(
                "echo",
                value =>
                {
                    callbackThreadId = Environment.CurrentManagedThreadId;
                    invocation = value;
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    callbackCompleted.Set();
                }));

        var stopwatch = Stopwatch.StartNew();
        var result = dispatcher.Dispatch("  EcHo\t alpha beta  ");
        stopwatch.Stop();

        try
        {
            Assert.Equal(CommandDispatchResult.Accepted, result);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                "Dispatch waited for a blocking handler instead of queuing it.");
            Assert.True(
                callbackStarted.Wait(TimeSpan.FromSeconds(5)),
                "The queued command was not processed.");
            Assert.NotEqual(
                callerThreadId,
                callbackThreadId,
                "The command handler ran inline on the dispatching thread.");

            var captured = Assert.NotNull(invocation);
            Assert.Equal("EcHo\t alpha beta", captured.Text);
            Assert.Equal("EcHo", captured.CommandName);
            Assert.Equal("alpha beta", captured.Arguments);
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.True(
            callbackCompleted.Wait(TimeSpan.FromSeconds(5)),
            "The command handler did not finish after release.");
        Assert.Equal(
            CommandDispatchResult.RejectedEmpty,
            dispatcher.Dispatch(" \t\r\n "));

        dispatcher.Dispose();
        Assert.Equal(
            CommandDispatchResult.Disposed,
            dispatcher.Dispatch("echo after-dispose"));

        return Task.CompletedTask;
    }

    private static Task TestManagedNativeAbiLayout()
    {
        var assembly = typeof(LuvLetterConfiguration).Assembly;
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeInputBoxConfig",
            104,
            [
                "StructSize", "AbiVersion", "Width", "Height", "CornerRadius",
                "BorderThickness", "FontSize", "HorizontalPadding", "VerticalPadding",
                "CaretWidth", "PositionMode", "OffsetX", "OffsetY", "BottomMargin",
                "CustomX", "CustomY", "BorderColor", "BackgroundColor", "TextColor",
                "CaretColor", "SubmitVirtualKey", "CancelVirtualKey", "BackspaceVirtualKey",
                "SubmitModifiers", "CancelModifiers", "BackspaceModifiers",
            ]);
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeFeatureWindowConfig",
            88,
            [
                "StructSize", "AbiVersion", "ItemsPerPage", "CellSize", "Gap",
                "CornerRadius", "BorderThickness", "FontSize", "BottomMargin", "OffsetX",
                "OffsetY", "BorderColor", "BackgroundColor", "TextColor", "AccentColor",
                "PreviousVirtualKey", "NextVirtualKey", "CancelVirtualKey",
                "FirstItemVirtualKey", "PreviousModifiers", "NextModifiers", "CancelModifiers",
            ]);
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeFeatureItem",
            16,
            ["Token", "Label"]);

        return Task.CompletedTask;
    }

    private static void AssertNativeLayout(
        Assembly assembly,
        string typeName,
        int expectedSize,
        IReadOnlyList<string> expectedFields)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        Assert.Equal(expectedSize, Marshal.SizeOf(type), $"Unexpected size for {typeName}.");
        var actualFields = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => field.MetadataToken)
            .Select(field => field.Name);
        Assert.SequenceEqual(expectedFields, actualFields, $"Unexpected field order for {typeName}.");
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

            var reloaded = new LuvLetterConfigurationStore(settingsPath).Current;
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

            var migrated = new LuvLetterConfigurationStore(legacyPath).Current;
            Assert.Equal(
                HotkeyModifierKeys.Control,
                migrated.InputBox.Hotkeys.Activation.Modifiers);
            Assert.Equal(75, migrated.InputBox.Hotkeys.Activation.VirtualKey);
            Assert.Equal("K", migrated.InputBox.Hotkeys.Activation.KeyName);
            Assert.Equal(
                ActivationGestureKind.DoubleControlPress,
                migrated.ActivationGestures.InputBox);

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
            Assert.Equal(10.0f, migratedVisual.InputBox.Size.CornerRadius);
            Assert.Equal("#66FFFFFF", migratedVisual.InputBox.Colors.Border);
            Assert.Equal(1.0f, migratedVisual.FeatureWindow.Layout.BorderThickness);
            Assert.Equal(16.0f, migratedVisual.FeatureWindow.Layout.CornerRadius);
            Assert.Equal("#66FFFFFF", migratedVisual.FeatureWindow.Colors.Border);

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

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new SmokeTestException(message ?? "Expected true, but found false.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, but found true.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new SmokeTestException(
                message ?? $"Expected <{expected}>, but found <{actual}>.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new SmokeTestException(
                message ?? $"Did not expect <{actual}>.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
        where T : class
    {
        if (value is null)
        {
            throw new SmokeTestException(message ?? "Expected a non-null value.");
        }

        return value;
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string? message = null)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new SmokeTestException(
                message
                ?? $"Expected [{string.Join(", ", expectedArray)}], "
                + $"but found [{string.Join(", ", actualArray)}].");
        }
    }

    public static void Empty<T>(IEnumerable<T> values, string? message = null)
    {
        if (values.Any())
        {
            throw new SmokeTestException(message ?? "Expected an empty sequence.");
        }
    }

    public static void AllFinite(params float[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new SmokeTestException(
                    $"Expected finite value at index {index}, but found {values[index]}.");
            }
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new SmokeTestException(
            $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}

internal sealed class SmokeTestException(string message) : Exception(message);
