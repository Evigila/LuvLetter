namespace LuvLetter.Core.NativeShell;

public enum InputMode
{
    General = 0,
    Ask = 1,
    Command = 2,
}

public sealed record InputSubmission(string Text, InputMode Mode);
