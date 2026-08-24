using System.Collections.ObjectModel;
using System.Globalization;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal static class SettingsChoiceCatalog
{
    public static IReadOnlyList<GestureChoice> Gestures { get; } =
    [
        new(ActivationGestureKind.DoubleControlPress, "Double-tap Ctrl"),
        new(ActivationGestureKind.ControlTapThenHold, "Tap Ctrl, then hold Ctrl"),
    ];

    public static ObservableCollection<FirstItemKeyChoice> CreateFirstItemKeyChoices() =>
        new(
            Enumerable.Range(0, 10).Select(
                digit => new FirstItemKeyChoice(
                    0x30 + digit,
                    digit.ToString(CultureInfo.InvariantCulture)
                )
            )
        );
}

internal sealed record GestureChoice(ActivationGestureKind Kind, string Label)
{
    public override string ToString() => Label;
}

internal sealed record FirstItemKeyChoice(int VirtualKey, string Label)
{
    public override string ToString() => Label;
}
