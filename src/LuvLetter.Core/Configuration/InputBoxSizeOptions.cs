namespace LuvLetter.Core.Configuration;

public sealed record InputBoxSizeOptions
{
    public const float DefaultHorizontalPadding = 10.0f;

    public const float DefaultVerticalPadding = 6.0f;

    public const float DefaultCaretWidth = 2.25f;

    public int Width { get; init; } = 640;

    public int Height { get; init; } = 44;

    public float CornerRadius { get; init; } = 10.0f;

    public float BorderThickness { get; init; } = 1.0f;

    public float FontSize { get; init; } = 20.0f;

    public float HorizontalPadding { get; init; } = DefaultHorizontalPadding;

    public float VerticalPadding { get; init; } = DefaultVerticalPadding;

    public float CaretWidth { get; init; } = DefaultCaretWidth;
}
