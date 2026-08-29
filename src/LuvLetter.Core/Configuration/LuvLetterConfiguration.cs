namespace LuvLetter.Core.Configuration;

public sealed record LuvLetterConfiguration
{
    public const int CurrentSchemaVersion = 11;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public InputBoxConfiguration InputBox { get; init; } = new();

    public ActivationGestureOptions ActivationGestures { get; init; } = new();

    public QuickActionsConfiguration QuickActions { get; init; } = new();

    public static LuvLetterConfiguration Default { get; } = new();
}
