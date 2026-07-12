namespace LuvLetter.Core.Configuration;

public sealed record FeatureWindowLayoutOptions
{
    public const int MaximumItemsPerPage = 7;

    public int ItemsPerPage { get; init; } = MaximumItemsPerPage;

    public float CellSize { get; init; } = 96.0f;

    public float Gap { get; init; } = 12.0f;

    public float CornerRadius { get; init; } = 16.0f;

    public float BorderThickness { get; init; } = 1.0f;

    public float FontSize { get; init; } = 16.0f;

    public int BottomMargin { get; init; } = 60;

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }
}
