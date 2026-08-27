using System.Runtime.InteropServices;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.NativeShell;
using LuvLetter.Core.Application;
using LuvLetter.Core.Plugins;

namespace LuvLetter.Core.Tests;

internal sealed class FakeNativeShellApi : INativeShellApi
{
    public uint AbiVersion => 3;

    public int CompatibilityChecks { get; private set; }

    public NativeInputSubmittedCallback? InputSubmittedCallback { get; private set; }

    public NativeFeatureActivatedCallback? QuickActionActivatedCallback { get; private set; }

    public NativeInputBoxConfig? LastInputBoxConfig { get; private set; }

    public NativeFeatureWindowConfig? LastQuickActionsConfig { get; private set; }

    public IReadOnlyList<(ulong Token, string Label)> QuickActionItems { get; private set; } = [];

    public int SetFeatureItemsResult { get; set; }

    public int ToggleInputBoxResult { get; set; }

    public int HidePopupsResult { get; set; }

    public int EnqueueMessageResult { get; set; }

    public int ToggleMessageQueueResult { get; set; }

    public int HideMessageQueueResult { get; set; }

    public int ShowInputBoxCalls { get; private set; }

    public int HideInputBoxCalls { get; private set; }

    public int ToggleQuickActionsCalls { get; private set; }

    public int HideQuickActionsCalls { get; private set; }

    public List<(string Text, int Length)> EnqueuedMessages { get; } = [];

    public int ToggleMessageQueueCalls { get; private set; }

    public int HideMessageQueueCalls { get; private set; }

    public int HidePopupsCalls { get; private set; }

    public int ShutdownCalls { get; private set; }

    public void EnsureCompatible() => CompatibilityChecks++;

    public int ApplyInputBoxConfig(in NativeInputBoxConfig config)
    {
        LastInputBoxConfig = config;
        return 0;
    }

    public int SetInputSubmittedCallback(
        NativeInputSubmittedCallback? callback,
        IntPtr context)
    {
        _ = context;
        InputSubmittedCallback = callback;
        return 0;
    }

    public int ShowInputBox()
    {
        ShowInputBoxCalls++;
        return 0;
    }

    public int HideInputBox()
    {
        HideInputBoxCalls++;
        return 0;
    }

    public int ToggleInputBox() => ToggleInputBoxResult;

    public int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config)
    {
        LastQuickActionsConfig = config;
        return 0;
    }

    public int SetFeatureItems(NativeFeatureItem[] items, int count)
    {
        var copiedItems = new (ulong Token, string Label)[count];
        for (var index = 0; index < count; index++)
        {
            copiedItems[index] = (
                items[index].Token,
                Marshal.PtrToStringUni(items[index].Label) ?? string.Empty);
        }

        QuickActionItems = copiedItems;
        return SetFeatureItemsResult;
    }

    public int SetFeatureActivatedCallback(
        NativeFeatureActivatedCallback? callback,
        IntPtr context)
    {
        _ = context;
        QuickActionActivatedCallback = callback;
        return 0;
    }

    public int ShowFeatureWindow() => 0;

    public int HideFeatureWindow()
    {
        HideQuickActionsCalls++;
        return 0;
    }

    public int ToggleFeatureWindow()
    {
        ToggleQuickActionsCalls++;
        return 0;
    }

    public int EnqueueMessage(string text, int length)
    {
        EnqueuedMessages.Add((text, length));
        return EnqueueMessageResult;
    }

    public int ToggleMessageQueue()
    {
        ToggleMessageQueueCalls++;
        return ToggleMessageQueueResult;
    }

    public int HideMessageQueue()
    {
        HideMessageQueueCalls++;
        return HideMessageQueueResult;
    }

    public int HidePopups()
    {
        HidePopupsCalls++;
        return HidePopupsResult;
    }

    public int ShutdownInputBox()
    {
        ShutdownCalls++;
        return 0;
    }

    public void RaiseFeatureActivated(ulong token) =>
        QuickActionActivatedCallback?.Invoke(token, IntPtr.Zero);

    public void RaiseInputSubmitted(string value)
    {
        var pointer = Marshal.StringToHGlobalUni(value);
        try
        {
            InputSubmittedCallback?.Invoke(pointer, value.Length, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}

internal sealed class FakeConfigurationStore(LuvLetterConfiguration current)
    : ILuvLetterConfigurationStore
{
    public ConfigurationLoadResult InitialLoad { get; } =
        new(current, ConfigurationLoadStatus.Loaded);

    public LuvLetterConfiguration Current { get; private set; } = current;

    public bool FailUpdates { get; init; }

    public LuvLetterConfiguration Update(LuvLetterConfiguration configuration)
    {
        if (FailUpdates)
        {
            throw new IOException("simulated persistence failure");
        }

        Current = configuration;
        return configuration;
    }
}

internal sealed class FakeActivationGestureService : IActivationGestureService
{
    public List<ActivationGestureOptions> AppliedOptions { get; } = [];

    public event EventHandler? CommandInputRequested;

    public event EventHandler? PopupsDismissRequested;

    public event EventHandler? QuickActionsRequested;

    public event EventHandler? MessageQueueToggleRequested;

    public int StopCalls { get; private set; }

    public void Start(ActivationGestureOptions options) => AppliedOptions.Add(options);

    public void Update(ActivationGestureOptions options) => AppliedOptions.Add(options);

    public void CancelPendingGestures()
    {
    }

    public void RaiseCommandInputRequested() =>
        CommandInputRequested?.Invoke(this, EventArgs.Empty);

    public void RaisePopupsDismissRequested() =>
        PopupsDismissRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseQuickActionsRequested() =>
        QuickActionsRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseMessageQueueToggleRequested() =>
        MessageQueueToggleRequested?.Invoke(this, EventArgs.Empty);

    public void Stop()
    {
        StopCalls++;
    }
}

internal sealed class FakeNativeShell : INativeShell
{
    public event Action<string>? InputSubmitted;

    public event Action<string>? QuickActionActivated;

    public event Action? QuickActionUnavailable;

    public int AppliedConfigurations { get; private set; }

    public List<IReadOnlyList<QuickActionSnapshot>> SynchronizedSnapshots { get; } = [];

    public int ToggleCommandInputCalls { get; private set; }

    public int HideCommandInputCalls { get; private set; }

    public int ToggleQuickActionsCalls { get; private set; }

    public int HideQuickActionsCalls { get; private set; }

    public List<string> EnqueuedMessages { get; } = [];

    public int ToggleMessageQueueCalls { get; private set; }

    public int HideMessageQueueCalls { get; private set; }

    public int HidePopupsCalls { get; private set; }

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration)
    {
        _ = inputBoxConfiguration;
        _ = quickActionsConfiguration;
        AppliedConfigurations++;
    }

    public void SynchronizeQuickActions(IReadOnlyList<QuickActionSnapshot> quickActions) =>
        SynchronizedSnapshots.Add(quickActions.ToArray());

    public void ToggleCommandInput() => ToggleCommandInputCalls++;

    public void HideCommandInput() => HideCommandInputCalls++;

    public void ToggleQuickActions() => ToggleQuickActionsCalls++;

    public void HideQuickActions() => HideQuickActionsCalls++;

    public void EnqueueMessage(string message) => EnqueuedMessages.Add(message);

    public void ToggleMessageQueue() => ToggleMessageQueueCalls++;

    public void HideMessageQueue() => HideMessageQueueCalls++;

    public void HidePopups() => HidePopupsCalls++;

    public void RaiseInputSubmitted(string commandText) => InputSubmitted?.Invoke(commandText);

    public void RaiseQuickActionActivated(string quickActionId) =>
        QuickActionActivated?.Invoke(quickActionId);

    public void RaiseQuickActionUnavailable() => QuickActionUnavailable?.Invoke();
}

internal sealed class FakeApplicationShell : IApplicationShell
{
    public int ShowSettingsCalls { get; private set; }

    public List<string> Statuses { get; } = [];

    public void ShowSettings() => ShowSettingsCalls++;

    public void ReportStatus(string message) => Statuses.Add(message);
}

internal sealed class FakeInputBoxConfigurationSink : INativeConfigurationSink
{
    public List<(
        InputBoxConfiguration InputBox,
        QuickActionsConfiguration QuickActions)> AppliedConfigurations
    { get; } = [];

    public int? FailOnApplyCall { get; init; }

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration)
    {
        if (AppliedConfigurations.Count + 1 == FailOnApplyCall)
        {
            throw new InvalidOperationException("simulated Native rollback failure");
        }

        AppliedConfigurations.Add((inputBoxConfiguration, quickActionsConfiguration));
    }
}

internal sealed class FakePlugin(
    string id,
    Action<PluginRegistrationContext>? register = null) : ILuvLetterPlugin, IDisposable
{
    public string Id { get; } = id;

    public bool IsDisposed { get; private set; }

    public void Register(PluginRegistrationContext context)
    {
        register?.Invoke(context);
    }

    public void Dispose() => IsDisposed = true;
}
