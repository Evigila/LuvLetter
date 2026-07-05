namespace LuvLetter.Core.Configuration;

public sealed record InputBoxColorOptions
{
    public string Border { get; init; } = "#FFFFFFFF";

    public string Background { get; init; } = "#66DCDCDC";

    public string Text { get; init; } = "#F2191919";

    public string Caret { get; init; } = "#F2191919";
}
