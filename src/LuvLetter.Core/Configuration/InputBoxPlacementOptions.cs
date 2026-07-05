namespace LuvLetter.Core.Configuration;

public sealed record InputBoxPlacementOptions
{
    public InputBoxPositionMode Mode { get; init; } = InputBoxPositionMode.CenterBottom;

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }

    public int BottomMargin { get; init; } = 120;

    public int CustomX { get; init; }

    public int CustomY { get; init; }
}
