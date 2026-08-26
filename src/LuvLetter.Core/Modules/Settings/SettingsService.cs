using LuvLetter.Core.Activation;

namespace LuvLetter.Core.Modules.Settings;

using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;

public enum ConfigurationApplicationStatus
{
    Applied,
    Rejected,
    FailedAndRestored,
    FailedRuntimeInconsistent,
}

public sealed record ConfigurationApplicationResult(
    ConfigurationApplicationStatus Status,
    string Message,
    LuvLetterConfiguration? DisplayConfiguration)
{
    public bool Succeeded => Status == ConfigurationApplicationStatus.Applied;

    public bool RequiresRestart =>
        Status == ConfigurationApplicationStatus.FailedRuntimeInconsistent;
}

/// <summary>
/// Minimal runtime capability used by the configuration transaction to update
/// both Native windows without exposing the rest of the Native shell surface.
/// </summary>
public interface INativeConfigurationSink
{
    void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration);
}

public interface ISettingsService
{
    LuvLetterConfiguration Current { get; }

    LuvLetterConfiguration CreateDefaultConfiguration();

    void CancelPendingGestures();

    ConfigurationApplicationResult Apply(LuvLetterConfiguration configuration);

    bool TryMap(
        LuvLetterConfiguration baseline,
        SettingsEditorInput input,
        out LuvLetterConfiguration configuration,
        out string error);

    LuvLetterConfiguration ReplaceHotkey(
        LuvLetterConfiguration configuration,
        SettingsHotkeyField field,
        HotkeyDefinition hotkey);
}

public sealed partial class SettingsService : ISettingsService
{
    private readonly ILuvLetterConfigurationStore configurationStore;
    private readonly IActivationGestureService activationService;
    private readonly INativeConfigurationSink nativeConfigurationSink;

    public SettingsService(
        ILuvLetterConfigurationStore configurationStore,
        IActivationGestureService activationService,
        INativeConfigurationSink nativeConfigurationSink)
    {
        this.configurationStore = configurationStore;
        this.activationService = activationService;
        this.nativeConfigurationSink = nativeConfigurationSink;
    }

    public LuvLetterConfiguration Current => configurationStore.Current;

    public LuvLetterConfiguration CreateDefaultConfiguration() =>
        LuvLetterConfigurationStore.Normalize(LuvLetterConfiguration.Default);

    public void CancelPendingGestures() => activationService.CancelPendingGestures();

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
                ConfigurationApplicationStatus.Rejected,
                $"Cannot normalize settings: {exception.Message}",
                DisplayConfiguration: null);
        }

        var previousConfiguration = configurationStore.Current;
        try
        {
            nativeConfigurationSink.ApplyConfiguration(
                normalizedConfiguration.InputBox,
                normalizedConfiguration.QuickActions);
            activationService.Update(normalizedConfiguration.ActivationGestures);
            var appliedConfiguration = configurationStore.Update(normalizedConfiguration);
            return new ConfigurationApplicationResult(
                ConfigurationApplicationStatus.Applied,
                "Applied",
                appliedConfiguration);
        }
        catch (Exception exception)
        {
            var rollbackError = TryRollback(previousConfiguration);
            var message = rollbackError is null
                ? $"Apply failed: {exception.Message}. Previous settings restored."
                : $"Apply failed: {exception.Message}. Runtime state may be inconsistent; "
                    + $"restart LuvLetter. Rollback warning: {rollbackError}";
            return new ConfigurationApplicationResult(
                rollbackError is null
                    ? ConfigurationApplicationStatus.FailedAndRestored
                    : ConfigurationApplicationStatus.FailedRuntimeInconsistent,
                message,
                previousConfiguration);
        }
    }

    private string? TryRollback(LuvLetterConfiguration previousConfiguration)
    {
        var failures = new List<string>();

        try
        {
            activationService.Update(previousConfiguration.ActivationGestures);
        }
        catch (Exception exception)
        {
            failures.Add($"gesture: {exception.Message}");
        }

        try
        {
            nativeConfigurationSink.ApplyConfiguration(
                previousConfiguration.InputBox,
                previousConfiguration.QuickActions);
        }
        catch (Exception exception)
        {
            failures.Add($"native windows: {exception.Message}");
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }
}
