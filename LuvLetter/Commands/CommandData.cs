namespace LuvLetter.Commands;

public abstract class CommandData(CommandMetadata metadata, Type responseBehaviorType)
{
    public CommandMetadata Metadata { get; } = metadata;

    public Type ResponseBehaviorType { get; } = responseBehaviorType;
}
