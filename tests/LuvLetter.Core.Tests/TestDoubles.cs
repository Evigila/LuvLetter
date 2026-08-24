using System.Runtime.InteropServices;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules;
using LuvLetter.Core.Native;

namespace LuvLetter.Core.Tests;

internal sealed class FakeNativeInputBoxApi : INativeInputBoxApi
{
    public uint AbiVersion => 73;

    public int CompatibilityChecks { get; private set; }

    public NativeInputSubmittedCallback? InputSubmittedCallback { get; private set; }

    public NativeFeatureActivatedCallback? FeatureActivatedCallback { get; private set; }

    public NativeInputBoxConfig? LastInputBoxConfig { get; private set; }

    public NativeFeatureWindowConfig? LastFeatureWindowConfig { get; private set; }

    public IReadOnlyList<(ulong Token, string Label)> FeatureItems { get; private set; } = [];

    public int SetFeatureItemsResult { get; set; }

    public int ToggleInputBoxResult { get; set; }

    public int ShowInputBoxCalls { get; private set; }

    public int HideInputBoxCalls { get; private set; }

    public int ToggleFeatureWindowCalls { get; private set; }

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
        LastFeatureWindowConfig = config;
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

        FeatureItems = copiedItems;
        return SetFeatureItemsResult;
    }

    public int SetFeatureActivatedCallback(
        NativeFeatureActivatedCallback? callback,
        IntPtr context)
    {
        _ = context;
        FeatureActivatedCallback = callback;
        return 0;
    }

    public int ShowFeatureWindow() => 0;

    public int HideFeatureWindow() => 0;

    public int ToggleFeatureWindow()
    {
        ToggleFeatureWindowCalls++;
        return 0;
    }

    public int ShutdownInputBox()
    {
        ShutdownCalls++;
        return 0;
    }

    public void RaiseFeatureActivated(ulong token) =>
        FeatureActivatedCallback?.Invoke(token, IntPtr.Zero);

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

    public void Update(ActivationGestureOptions options) => AppliedOptions.Add(options);

    public void CancelPendingGestures()
    {
    }
}

internal sealed class FakeInputBoxConfigurationSink : IInputBoxConfigurationSink
{
    public List<(
        InputBoxConfiguration InputBox,
        FeatureWindowConfiguration FeatureWindow)> AppliedConfigurations
    { get; } = [];

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration) =>
        AppliedConfigurations.Add((inputBoxConfiguration, featureWindowConfiguration));
}

internal sealed class FakeModule(
    string id,
    Action<ModuleRegistrationContext>? register = null) : ILuvLetterModule, IDisposable
{
    public string Id { get; } = id;

    public bool IsDisposed { get; private set; }

    public void Register(ModuleRegistrationContext context)
    {
        register?.Invoke(context);
    }

    public void Dispose() => IsDisposed = true;
}
