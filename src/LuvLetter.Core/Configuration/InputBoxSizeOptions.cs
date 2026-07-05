namespace LuvLetter.Core.Configuration;

public sealed record InputBoxSizeOptions
{
    public int Width { get; init; } = 640;

    public int Height { get; init; } = 56;

    public float CornerRadius { get; init; } = 8.0f;

    public float BorderThickness { get; init; } = 2.0f;

    public float FontSize { get; init; } = 20.0f;

    public float HorizontalPadding { get; init; } = 18.0f;
}
