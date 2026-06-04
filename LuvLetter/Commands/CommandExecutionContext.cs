namespace LuvLetter.Commands;

public sealed record CommandExecutionContext(CommandData Command, CommandRequest Request);
