namespace LuvLetter.Core.Features;

public sealed class FeatureDefinition
{
    public FeatureDefinition(string id, string displayName, Action activate)
        : this(id, displayName, WrapSynchronousActivation(activate))
    {
    }

    public FeatureDefinition(
        string id,
        string displayName,
        Func<CancellationToken, ValueTask> activateAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(activateAsync);

        Id = id.Trim();
        DisplayName = displayName.Trim();
        ActivateAsync = activateAsync;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Func<CancellationToken, ValueTask> ActivateAsync { get; }

    private static Func<CancellationToken, ValueTask> WrapSynchronousActivation(
        Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        return _ =>
        {
            activate();
            return ValueTask.CompletedTask;
        };
    }
}
