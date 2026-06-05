using System.Runtime.InteropServices;

namespace LuvLetter.Overlay.Native;

internal static class NativeOverlayApi
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void NativeOverlayEventCallback(IntPtr eventData, IntPtr context);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "StartOverlay",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int StartOverlay(in NativeOverlayStartOptions options);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "StopOverlay",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern void StopOverlay();

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayLayout",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int UpdateOverlayLayout(in NativeOverlayLayoutConfig layoutConfig);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayLogo",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int UpdateOverlayLogo(IntPtr logoData, int logoSize);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayInputText",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode
    )]
    internal static extern int UpdateOverlayInputText(string? text, int textLength);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayInputPromptText",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode
    )]
    internal static extern int UpdateOverlayInputPromptText(string? text, int textLength);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayInputSelection",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int UpdateOverlayInputSelection(
        int selectionStart,
        int selectionLength,
        int caretIndex
    );

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayOutputText",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode
    )]
    internal static extern int UpdateOverlayOutputText(string? text, int textLength);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "UpdateOverlayOutputNavigation",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int UpdateOverlayOutputNavigation(
        [MarshalAs(UnmanagedType.Bool)] bool canPageUp,
        [MarshalAs(UnmanagedType.Bool)] bool canPageDown
    );

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "SetOverlayVisualMode",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern int SetOverlayVisualMode(int visualMode);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "SetOverlayEventCallback",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    internal static extern void SetOverlayEventCallback(
        NativeOverlayEventCallback? callback,
        IntPtr context);
}
