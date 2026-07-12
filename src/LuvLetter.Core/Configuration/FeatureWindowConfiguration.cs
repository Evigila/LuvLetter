namespace LuvLetter.Core.Configuration;

public sealed record FeatureWindowConfiguration
{
    public FeatureWindowLayoutOptions Layout { get; init; } = new();

    public FeatureWindowColorOptions Colors { get; init; } = new();

    public FeatureWindowHotkeyOptions Hotkeys { get; init; } = new();
}
