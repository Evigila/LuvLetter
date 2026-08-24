namespace LuvLetter.Core.Configuration;

public sealed record InputBoxColorOptions
{
    public string Border { get; init; } = "#66FFFFFF";

    public string Background { get; init; } = "#80F5F5F5";

    public float BackgroundOpacity { get; init; } = 0.5f;

    public string Text { get; init; } = "#FFFFFFFF";

    public float TextOpacity { get; init; } = 1.0f;

    public string Caret { get; init; } = "#FFFFFFFF";
}
