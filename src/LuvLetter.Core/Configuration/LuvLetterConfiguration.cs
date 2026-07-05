namespace LuvLetter.Core.Configuration;

public sealed record LuvLetterConfiguration
{
    public InputBoxConfiguration InputBox { get; init; } = new();

    public static LuvLetterConfiguration Default { get; } = new();
}
