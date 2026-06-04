namespace LuvLetter.Commands;

public sealed record CommandRequest(string RawText, string Name, string Arguments)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name);

    public static CommandRequest Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
