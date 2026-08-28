using System.Runtime.InteropServices;

namespace LuvLetter.Core.NativeShell;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInputBoxConfig
{
    public uint StructSize;
    public uint AbiVersion;
    public int Width;
    public int Height;
    public float CornerRadius;
    public float BorderThickness;
    public float FontSize;
    public float HorizontalPadding;
    public float VerticalPadding;
    public float CaretWidth;
    public int PositionMode;
    public int OffsetX;
    public int OffsetY;
    public int BottomMargin;
    public int CustomX;
    public int CustomY;
    public uint BorderColor;
    public uint BackgroundColor;
    public uint TextColor;
    public uint CaretColor;
    public int SubmitVirtualKey;
    public int CancelVirtualKey;
    public int BackspaceVirtualKey;
    public int SubmitModifiers;
    public int CancelModifiers;
    public int BackspaceModifiers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFeatureWindowConfig
{
    public uint StructSize;
    public uint AbiVersion;
    public int ItemsPerPage;
    public float CellSize;
    public float Gap;
    public float CornerRadius;
    public float BorderThickness;
    public float FontSize;
    public int BottomMargin;
    public int OffsetX;
    public int OffsetY;
    public uint BorderColor;
    public uint BackgroundColor;
    public uint TextColor;
    public uint AccentColor;
    public int PreviousVirtualKey;
    public int NextVirtualKey;
    public int CancelVirtualKey;
    public int FirstItemVirtualKey;
    public int PreviousModifiers;
    public int NextModifiers;
    public int CancelModifiers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFeatureItem
{
    public ulong Token;
    public IntPtr Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInputCandidate
{
    public ulong Token;
    public int Kind;
    public int IconKind;
    public IntPtr PrimaryText;
    public IntPtr SecondaryText;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeFeatureActivatedCallback(ulong token, IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeInputSubmittedCallback(
    IntPtr text,
    int length,
    int inputMode,
    IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeInputChangedCallback(
    IntPtr text,
    int length,
    int inputMode,
    ulong revision,
    IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeCandidateActivatedCallback(
    ulong token,
    int action,
    IntPtr context);

internal sealed class NativeShellApi : INativeShellApi
{
    private const uint CurrentAbiVersion = 6;

    internal static INativeShellApi Instance { get; } = new NativeShellApi();

    private NativeShellApi()
    {
    }

    public uint AbiVersion => CurrentAbiVersion;

    private static class NativeMethods
    {
        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "GetNativeApiVersion",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetNativeApiVersion();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ApplyInputBoxConfig",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ApplyInputBoxConfig(in NativeInputBoxConfig config);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetInputSubmittedCallback",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetInputSubmittedCallback(
            NativeInputSubmittedCallback? callback,
            IntPtr context);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetInputChangedCallback",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetInputChangedCallback(
            NativeInputChangedCallback? callback,
            IntPtr context);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetCandidateActivatedCallback",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetCandidateActivatedCallback(
            NativeCandidateActivatedCallback? callback,
            IntPtr context);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetInputCandidates",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetInputCandidates(
            [In] NativeInputCandidate[] items,
            int count,
            ulong revision);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ShowInputBox",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ShowInputBox();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "HideInputBox",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int HideInputBox();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ToggleInputBox",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ToggleInputBox();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ApplyFeatureWindowConfig",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetFeatureItems",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetFeatureItems(
            [In] NativeFeatureItem[] items,
            int count);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "SetFeatureActivatedCallback",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetFeatureActivatedCallback(
            NativeFeatureActivatedCallback? callback,
            IntPtr context);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ShowFeatureWindow",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ShowFeatureWindow();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "HideFeatureWindow",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int HideFeatureWindow();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ToggleFeatureWindow",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ToggleFeatureWindow();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "EnqueueMessage",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int EnqueueMessage(
            [MarshalAs(UnmanagedType.LPWStr)] string text,
            int length);

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ToggleMessageQueue",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ToggleMessageQueue();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "HideMessageQueue",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int HideMessageQueue();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "HidePopups",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int HidePopups();

        [DllImport(
            "LuvLetter.Native.dll",
            EntryPoint = "ShutdownInputBox",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern int ShutdownInputBox();
    }

    public int ApplyInputBoxConfig(in NativeInputBoxConfig config) =>
        NativeMethods.ApplyInputBoxConfig(in config);

    public int SetInputSubmittedCallback(
        NativeInputSubmittedCallback? callback,
        IntPtr context) =>
        NativeMethods.SetInputSubmittedCallback(callback, context);

    public int SetInputChangedCallback(
        NativeInputChangedCallback? callback,
        IntPtr context) =>
        NativeMethods.SetInputChangedCallback(callback, context);

    public int SetCandidateActivatedCallback(
        NativeCandidateActivatedCallback? callback,
        IntPtr context) =>
        NativeMethods.SetCandidateActivatedCallback(callback, context);

    public int SetInputCandidates(
        NativeInputCandidate[] items,
        int count,
        ulong revision) =>
        NativeMethods.SetInputCandidates(items, count, revision);

    public int ShowInputBox() => NativeMethods.ShowInputBox();

    public int HideInputBox() => NativeMethods.HideInputBox();

    public int ToggleInputBox() => NativeMethods.ToggleInputBox();

    public int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config) =>
        NativeMethods.ApplyFeatureWindowConfig(in config);

    public int SetFeatureItems(NativeFeatureItem[] items, int count) =>
        NativeMethods.SetFeatureItems(items, count);

    public int SetFeatureActivatedCallback(
        NativeFeatureActivatedCallback? callback,
        IntPtr context) =>
        NativeMethods.SetFeatureActivatedCallback(callback, context);

    public int ShowFeatureWindow() => NativeMethods.ShowFeatureWindow();

    public int HideFeatureWindow() => NativeMethods.HideFeatureWindow();

    public int ToggleFeatureWindow() => NativeMethods.ToggleFeatureWindow();

    public int EnqueueMessage(string text, int length) =>
        NativeMethods.EnqueueMessage(text, length);

    public int ToggleMessageQueue() => NativeMethods.ToggleMessageQueue();

    public int HideMessageQueue() => NativeMethods.HideMessageQueue();

    public int HidePopups() => NativeMethods.HidePopups();

    public int ShutdownInputBox() => NativeMethods.ShutdownInputBox();

    public void EnsureCompatible()
    {
        var actualVersion = NativeMethods.GetNativeApiVersion();
        if (actualVersion != CurrentAbiVersion)
        {
            throw new InvalidOperationException(
                $"LuvLetter.Native ABI mismatch. Managed={CurrentAbiVersion}, Native={actualVersion}.");
        }

        EnsureSize<NativeInputBoxConfig>(104);
        EnsureSize<NativeFeatureWindowConfig>(88);
        EnsureSize<NativeFeatureItem>(16);
        EnsureSize<NativeInputCandidate>(32);
    }

    private static void EnsureSize<T>(int expected)
        where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Managed native structure {typeof(T).Name} has size {actual}; expected {expected}.");
        }
    }
}
