namespace LuvLetter.Commands;

public sealed record ParsedCommand(string RawText, string Name, string Arguments)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name);

    public static ParsedCommand Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
