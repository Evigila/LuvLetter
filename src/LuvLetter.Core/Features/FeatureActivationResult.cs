namespace LuvLetter.Core.Features;

public enum FeatureActivationStatus
{
    Succeeded,
    NotFound,
    Canceled,
    Failed,
}

public sealed record FeatureActivationResult(
    FeatureActivationStatus Status,
    Exception? Exception = null)
{
    public bool Succeeded => Status == FeatureActivationStatus.Succeeded;
}
