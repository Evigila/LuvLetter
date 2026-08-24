namespace LuvLetter.Core.Features;

/// <summary>
/// Minimal feature registration capability exposed to module infrastructure.
/// </summary>
public interface IFeatureRegistrar
{
    bool Register(
        FeatureDefinition feature,
        FeatureRegistrationMode mode = FeatureRegistrationMode.RejectDuplicate);

    bool IsRegistered(string id);
}
