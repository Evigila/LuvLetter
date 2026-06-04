namespace LuvLetter.Commands;

public sealed record CommandExecutionResult(string OutputText)
{
    public static CommandExecutionResult Empty { get; } = new(string.Empty);
}
