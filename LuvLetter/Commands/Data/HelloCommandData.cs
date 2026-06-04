using LuvLetter.Commands.Behaviors;

namespace LuvLetter.Commands.Data;

public sealed class HelloCommandData : CommandData
{
    public HelloCommandData()
        : base(HelloCommandMetadata.Instance, typeof(HelloCommandResponseBehavior)) { }
}
