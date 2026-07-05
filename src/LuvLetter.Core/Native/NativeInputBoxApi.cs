using System.Runtime.InteropServices;

namespace LuvLetter.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInputBoxConfig
{
    public int Width;
    public int Height;
    public float CornerRadius;
    public float BorderThickness;
    public float FontSize;
    public float HorizontalPadding;
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
}

internal static class NativeInputBoxApi
{
    [DllImport(
        "LuvLetter.Native.dll",
        EntryPoint = "ApplyInputBoxConfig",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int ApplyInputBoxConfig(in NativeInputBoxConfig config);

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
        EntryPoint = "ShutdownInputBox",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void ShutdownInputBox();

    internal static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Native input box call failed. HRESULT: 0x{result:X8}");
        }
    }
}
