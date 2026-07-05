namespace LuvLetter.Core.Configuration;

public sealed record InputBoxConfiguration
{
    public InputBoxHotkeyOptions Hotkeys { get; init; } = new();

    public InputBoxPlacementOptions Placement { get; init; } = new();

    public InputBoxColorOptions Colors { get; init; } = new();

    public InputBoxSizeOptions Size { get; init; } = new();
}
