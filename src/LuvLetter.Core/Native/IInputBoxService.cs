using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;

namespace LuvLetter.Core.Native;

public interface IInputBoxService : IDisposable
{
    event Action<string>? InputSubmitted;

    event Action<string>? FeatureActivated;

    void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration);

    void SynchronizeFeatures(IReadOnlyList<FeatureDefinition> features);

    void Show();

    void Hide();

    void Toggle();

    void ShowFeatureWindow();

    void HideFeatureWindow();

    void ToggleFeatureWindow();
}
