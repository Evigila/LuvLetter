namespace LuvLetter.Core.Commands;

public sealed record CommandInvocation(
    string Text,
    string CommandName,
    string Arguments);
