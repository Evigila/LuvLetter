namespace LuvLetter.Configuration;

public sealed record OverlayLayoutOptions
{
    public int OverlayWidth { get; init; } = 50;
    public int OverlayHeight { get; init; } = 50;
    public int CommandBarWidth { get; init; } = 420;
    public int ScreenMarginLeft { get; init; } = 20;
    public int ScreenMarginBottom { get; init; } = 20;
    public float ContentPaddingLeft { get; init; } = 8.0f;
    public float ContentPaddingTop { get; init; } = 8.0f;
    public float ContentPaddingRight { get; init; } = 8.0f;
    public float ContentPaddingBottom { get; init; } = 8.0f;
    public float LogoWidth { get; init; } = 0.0f;
    public float LogoHeight { get; init; } = 0.0f;
    public float LogoOffsetX { get; init; } = 0.0f;
    public float LogoOffsetY { get; init; } = 0.0f;
    public float CourtesyZoneOffsetX { get; init; } = 0.0f;
    public float CourtesyZoneOffsetY { get; init; } = 0.0f;
    public float CourtesyZoneWidth { get; init; } = 0.0f;
    public float CourtesyZoneHeight { get; init; } = 0.0f;
    public uint BadgeInactiveDelayMs { get; init; } = 5000;
    public float BadgeInactiveOpacity { get; init; } = 0.5f;
    public float CommandOutputHeight { get; init; } = 120.0f;
    public float TextReservedHeight { get; init; } = 0.0f;
    public float ElementGap { get; init; } = 8.0f;
    public uint AnimationDurationMs { get; init; } = 180;
}
