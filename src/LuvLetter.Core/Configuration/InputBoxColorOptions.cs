namespace LuvLetter.Core.Configuration;

public sealed record InputBoxColorOptions
{
    public string Border { get; init; } = "#66FFFFFF";

    public string Background { get; init; } = "#38F5F5F5";

    public float BackgroundOpacity { get; init; } = 0.22f;

    public string Text { get; init; } = "#FFFFFFFF";

    public string Caret { get; init; } = "#FFFFFFFF";
}
