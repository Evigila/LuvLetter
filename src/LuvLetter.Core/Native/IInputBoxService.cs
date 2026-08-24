using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;

namespace LuvLetter.Core.Native;

public interface IInputBoxConfigurationSink
{
    void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration);
}

public interface ICommandInputBox
{
    event Action<string>? InputSubmitted;

    void Show();

    void Hide();

    void Toggle();
}

public interface IFeatureWindow
{
    event Action<string>? FeatureActivated;

    void SynchronizeFeatures(IReadOnlyList<FeatureItemSnapshot> features);

    void ShowFeatureWindow();

    void HideFeatureWindow();

    void ToggleFeatureWindow();
}

public interface IInputBoxService :
    IInputBoxConfigurationSink,
    ICommandInputBox,
    IFeatureWindow,
    IDisposable
{
    long DroppedNotificationCount { get; }
}
