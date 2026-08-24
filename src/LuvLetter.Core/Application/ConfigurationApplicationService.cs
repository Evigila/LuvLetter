using LuvLetter.Core.Configuration;
using LuvLetter.Core.Native;
using LuvLetter.Core.Activation;

namespace LuvLetter.Core.Application;

public sealed class ConfigurationApplicationService
{
    private readonly ILuvLetterConfigurationStore configurationStore;
    private readonly IActivationGestureService hotkeyService;
    private readonly IInputBoxConfigurationSink inputBoxConfigurationSink;

    public ConfigurationApplicationService(
        ILuvLetterConfigurationStore configurationStore,
        IActivationGestureService hotkeyService,
        IInputBoxConfigurationSink inputBoxConfigurationSink)
    {
        this.configurationStore = configurationStore;
        this.hotkeyService = hotkeyService;
        this.inputBoxConfigurationSink = inputBoxConfigurationSink;
    }

    public LuvLetterConfiguration Current => configurationStore.Current;

    public LuvLetterConfiguration CreateDefaultConfiguration() =>
        LuvLetterConfigurationStore.Normalize(LuvLetterConfiguration.Default);

    public void CancelPendingGestures() => hotkeyService.CancelPendingGestures();

    public ConfigurationApplicationResult Apply(LuvLetterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LuvLetterConfiguration normalizedConfiguration;
        try
        {
            normalizedConfiguration = LuvLetterConfigurationStore.Normalize(configuration);
        }
        catch (Exception exception)
        {
            return new ConfigurationApplicationResult(
                Succeeded: false,
                $"Cannot normalize settings: {exception.Message}",
                DisplayConfiguration: null);
        }

        var previousConfiguration = configurationStore.Current;
        try
        {
            inputBoxConfigurationSink.ApplyConfiguration(
                normalizedConfiguration.InputBox,
                normalizedConfiguration.FeatureWindow);
            hotkeyService.Update(normalizedConfiguration.ActivationGestures);
            var appliedConfiguration = configurationStore.Update(normalizedConfiguration);
            return new ConfigurationApplicationResult(
                Succeeded: true,
                "Applied",
                appliedConfiguration);
        }
        catch (Exception exception)
        {
            var rollbackError = TryRollback(previousConfiguration);
            var message = rollbackError is null
                ? $"Apply failed: {exception.Message}. Previous settings restored."
                : $"Apply failed: {exception.Message}. Rollback warning: {rollbackError}";
            return new ConfigurationApplicationResult(
                Succeeded: false,
                message,
                previousConfiguration);
        }
    }

    private string? TryRollback(LuvLetterConfiguration previousConfiguration)
    {
        var failures = new List<string>();

        try
        {
            hotkeyService.Update(previousConfiguration.ActivationGestures);
        }
        catch (Exception exception)
        {
            failures.Add($"gesture: {exception.Message}");
        }

        try
        {
            inputBoxConfigurationSink.ApplyConfiguration(
                previousConfiguration.InputBox,
                previousConfiguration.FeatureWindow);
        }
        catch (Exception exception)
        {
            failures.Add($"native windows: {exception.Message}");
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }
}
