using System.Globalization;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestSettingsEditorMapping()
    {
        var defaults = LuvLetterConfiguration.Default;
        var service = new SettingsService(
            new FakeConfigurationStore(defaults),
            new FakeActivationGestureService(),
            new FakeInputBoxConfigurationSink());
        var input = CreateSettingsInput(defaults);

        Assert.True(service.TryMap(defaults, input, out var mapped, out var error), error);
        Assert.Equal(defaults.InputBox.Size.Width, mapped.InputBox.Size.Width);
        Assert.Equal(defaults.QuickActions.Layout.ItemsPerPage, mapped.QuickActions.Layout.ItemsPerPage);

        var duplicateGestureInput = input with
        {
            ActivationGestures = input.ActivationGestures with
            {
                QuickActionsGesture = input.ActivationGestures.InputBoxGesture,
            },
        };
        Assert.False(
            service.TryMap(defaults, duplicateGestureInput, out _, out _),
            "The editor accepted duplicate activation gestures.");

        var nonFiniteInput = input with
        {
            InputBox = input.InputBox with { FontSize = "NaN" },
        };
        Assert.False(
            service.TryMap(defaults, nonFiniteInput, out _, out _),
            "The editor accepted a non-finite floating-point value.");

        var replacement = new HotkeyDefinition(HotkeyModifierKeys.Control, 0x4A, "J");
        var replaced = service.ReplaceHotkey(
            defaults,
            SettingsHotkeyField.QuickActionsNextPage,
            replacement);
        Assert.Equal(replacement, replaced.QuickActions.Hotkeys.NextPage);
        Assert.Equal(
            defaults.QuickActions.Hotkeys.PreviousPage,
            replaced.QuickActions.Hotkeys.PreviousPage);

        return Task.CompletedTask;
    }

    private static SettingsEditorInput CreateSettingsInput(LuvLetterConfiguration configuration)
    {
        var input = configuration.InputBox;
        var gestures = configuration.ActivationGestures;
        var quickActions = configuration.QuickActions;
        return new(
            new(
                input.Placement.Mode,
                Invariant(input.Placement.OffsetX),
                Invariant(input.Placement.OffsetY),
                Invariant(input.Placement.BottomMargin),
                Invariant(input.Placement.CustomX),
                Invariant(input.Placement.CustomY),
                input.Colors.Border,
                input.Colors.Background,
                Invariant(input.Colors.BackgroundOpacity),
                input.Colors.Text,
                Invariant(input.Colors.TextOpacity),
                input.Colors.Caret,
                Invariant(input.Size.Width),
                Invariant(input.Size.Height),
                Invariant(input.Size.FontSize),
                Invariant(input.Size.CornerRadius),
                Invariant(input.Size.BorderThickness),
                Invariant(input.Size.HorizontalPadding),
                Invariant(input.Size.VerticalPadding),
                Invariant(input.Size.CaretWidth)),
            new(
                gestures.InputBox,
                gestures.QuickActions,
                Invariant(gestures.TapMaxDurationMs),
                Invariant(gestures.SecondPressTimeoutMs),
                Invariant(gestures.HoldThresholdMs),
                gestures.AllowLeftControl,
                gestures.AllowRightControl),
            new(
                quickActions.Hotkeys.FirstItemVirtualKey,
                Invariant(quickActions.Layout.ItemsPerPage),
                Invariant(quickActions.Layout.CellSize),
                Invariant(quickActions.Layout.Gap),
                Invariant(quickActions.Layout.CornerRadius),
                Invariant(quickActions.Layout.BorderThickness),
                Invariant(quickActions.Layout.FontSize),
                Invariant(quickActions.Layout.BottomMargin),
                Invariant(quickActions.Layout.OffsetX),
                Invariant(quickActions.Layout.OffsetY),
                quickActions.Colors.Border,
                quickActions.Colors.Accent,
                quickActions.Colors.Background,
                Invariant(quickActions.Colors.BackgroundOpacity),
                quickActions.Colors.Text,
                Invariant(quickActions.Colors.TextOpacity)));
    }

    private static string Invariant<T>(T value)
        where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
}
