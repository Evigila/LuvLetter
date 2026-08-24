using System.Runtime.InteropServices;

namespace LuvLetter.Core.Native;

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

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeFeatureActivatedCallback(ulong token, IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void NativeInputSubmittedCallback(IntPtr text, int length, IntPtr context);

internal static class NativeInputBoxApi
{
    internal const uint AbiVersion = 1;

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
        EntryPoint = "ShutdownInputBox",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int ShutdownInputBox();

    internal static void EnsureCompatible()
    {
        var actualVersion = GetNativeApiVersion();
        if (actualVersion != AbiVersion)
        {
            throw new InvalidOperationException(
                $"LuvLetter.Native ABI mismatch. Managed={AbiVersion}, Native={actualVersion}.");
        }

        EnsureSize<NativeInputBoxConfig>(104);
        EnsureSize<NativeFeatureWindowConfig>(88);
        EnsureSize<NativeFeatureItem>(16);
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
