namespace LuvLetter.Commands.Data;

public static class HelloCommandMetadata
{
    public static CommandMetadata Instance { get; } =
        new(
            Name: "hello",
            Description: "Returns a Hello World message.",
            DefaultTarget: CommandExecutionTarget.Managed,
            Aliases: []
        );
}
