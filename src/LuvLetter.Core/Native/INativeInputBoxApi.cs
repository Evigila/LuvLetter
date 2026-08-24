namespace LuvLetter.Core.Native;

internal interface INativeInputBoxApi
{
    uint AbiVersion { get; }

    void EnsureCompatible();

    int ApplyInputBoxConfig(in NativeInputBoxConfig config);

    int SetInputSubmittedCallback(NativeInputSubmittedCallback? callback, IntPtr context);

    int ShowInputBox();

    int HideInputBox();

    int ToggleInputBox();

    int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config);

    int SetFeatureItems(NativeFeatureItem[] items, int count);

    int SetFeatureActivatedCallback(NativeFeatureActivatedCallback? callback, IntPtr context);

    int ShowFeatureWindow();

    int HideFeatureWindow();

    int ToggleFeatureWindow();

    int ShutdownInputBox();
}

internal sealed class NativeInputBoxApiAdapter : INativeInputBoxApi
{
    internal static INativeInputBoxApi Instance { get; } = new NativeInputBoxApiAdapter();

    private NativeInputBoxApiAdapter()
    {
    }

    public uint AbiVersion => NativeInputBoxApi.AbiVersion;

    public void EnsureCompatible() => NativeInputBoxApi.EnsureCompatible();

    public int ApplyInputBoxConfig(in NativeInputBoxConfig config) =>
        NativeInputBoxApi.ApplyInputBoxConfig(in config);

    public int SetInputSubmittedCallback(
        NativeInputSubmittedCallback? callback,
        IntPtr context) =>
        NativeInputBoxApi.SetInputSubmittedCallback(callback, context);

    public int ShowInputBox() => NativeInputBoxApi.ShowInputBox();

    public int HideInputBox() => NativeInputBoxApi.HideInputBox();

    public int ToggleInputBox() => NativeInputBoxApi.ToggleInputBox();

    public int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config) =>
        NativeInputBoxApi.ApplyFeatureWindowConfig(in config);

    public int SetFeatureItems(NativeFeatureItem[] items, int count) =>
        NativeInputBoxApi.SetFeatureItems(items, count);

    public int SetFeatureActivatedCallback(
        NativeFeatureActivatedCallback? callback,
        IntPtr context) =>
        NativeInputBoxApi.SetFeatureActivatedCallback(callback, context);

    public int ShowFeatureWindow() => NativeInputBoxApi.ShowFeatureWindow();

    public int HideFeatureWindow() => NativeInputBoxApi.HideFeatureWindow();

    public int ToggleFeatureWindow() => NativeInputBoxApi.ToggleFeatureWindow();

    public int ShutdownInputBox() => NativeInputBoxApi.ShutdownInputBox();
}
