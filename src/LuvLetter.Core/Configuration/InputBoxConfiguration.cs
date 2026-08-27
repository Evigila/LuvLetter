using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed record InputBoxConfiguration
{
    public InputBoxHotkeyOptions Hotkeys { get; init; } = new();

    public InputBoxPlacementOptions Placement { get; init; } = new();

    public InputBoxColorOptions Colors { get; init; } = new();

    public InputBoxSizeOptions Size { get; init; } = new();
}

public enum InputBoxPositionMode
{
    CenterBottom = 0,
    Center = 1,
    CenterTop = 2,
    Custom = 3,
}

public sealed record InputBoxPlacementOptions
{
    public InputBoxPositionMode Mode { get; init; } = InputBoxPositionMode.CenterBottom;

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }

    public int BottomMargin { get; init; } = 60;

    public int CustomX { get; init; }

    public int CustomY { get; init; }
}

public sealed record InputBoxColorOptions
{
    public string Border { get; init; } = SurfaceStyleDefaults.Border;

    public string Background { get; init; } = SurfaceStyleDefaults.Background;

    public float BackgroundOpacity { get; init; } = SurfaceStyleDefaults.BackgroundOpacity;

    public string Text { get; init; } = SurfaceStyleDefaults.Content;

    public float TextOpacity { get; init; } = SurfaceStyleDefaults.ContentOpacity;

    public string Caret { get; init; } = SurfaceStyleDefaults.Content;
}

public sealed record InputBoxSizeOptions
{
    public const float DefaultHorizontalPadding = 10.0f;
    public const float DefaultVerticalPadding = 4.0f;
    public const float DefaultCaretWidth = 2.25f;

    public int Width { get; init; } = 560;

    public int Height { get; init; } = 32;

    public float CornerRadius { get; init; } = SurfaceStyleDefaults.CornerRadius;

    public float BorderThickness { get; init; } = SurfaceStyleDefaults.BorderThickness;

    public float FontSize { get; init; } = 14.0f;

    public float HorizontalPadding { get; init; } = DefaultHorizontalPadding;

    public float VerticalPadding { get; init; } = DefaultVerticalPadding;

    public float CaretWidth { get; init; } = DefaultCaretWidth;
}

public sealed record InputBoxHotkeyOptions
{
    public HotkeyDefinition Submit { get; init; } =
        new(HotkeyModifierKeys.None, 0x0D, "Enter");

    public HotkeyDefinition Cancel { get; init; } =
        new(HotkeyModifierKeys.None, 0x1B, "Escape");

    public HotkeyDefinition Backspace { get; init; } =
        new(HotkeyModifierKeys.None, 0x08, "Backspace");
}
