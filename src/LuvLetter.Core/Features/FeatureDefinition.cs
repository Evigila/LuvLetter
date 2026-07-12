namespace LuvLetter.Core.Features;

public sealed class FeatureDefinition
{
    public FeatureDefinition(string id, string displayName, Action activate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(activate);

        Id = id.Trim();
        DisplayName = displayName.Trim();
        Activate = activate;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Action Activate { get; }
}
