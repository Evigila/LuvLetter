using System.Runtime.InteropServices;

namespace LuvLetter.Overlay.Native;

internal enum NativeOverlayEventKind
{
    None = 0,
    InputChanged = 1,
    CommandSubmitted = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlayLayoutConfig
{
    public int OverlayWidth;
    public int OverlayHeight;
    public int CommandBarWidth;
    public int ScreenMarginLeft;
    public int ScreenMarginBottom;
    public float ContentPaddingLeft;
    public float ContentPaddingTop;
    public float ContentPaddingRight;
    public float ContentPaddingBottom;
    public float LogoWidth;
    public float LogoHeight;
    public float LogoOffsetX;
    public float LogoOffsetY;
    public float CourtesyZoneOffsetX;
    public float CourtesyZoneOffsetY;
    public float CourtesyZoneWidth;
    public float CourtesyZoneHeight;
    public uint BadgeInactiveDelayMs;
    public float BadgeInactiveOpacity;
    public float CommandOutputHeight;
    public float TextReservedHeight;
    public float ElementGap;
    public uint AnimationDurationMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlayStartOptions
{
    public IntPtr LogoData;
    public int LogoSize;
    public NativeOverlayLayoutConfig LayoutConfig;
    public IntPtr InitialInputText;
    public int InitialInputTextLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlayEvent
{
    public NativeOverlayEventKind Kind;
    public IntPtr Text;
    public int TextLength;
}
