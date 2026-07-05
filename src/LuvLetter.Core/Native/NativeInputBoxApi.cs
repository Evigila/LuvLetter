using System.Runtime.InteropServices;

namespace LuvLetter.Core.Native;

internal static class NativeInputBoxApi
{
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
