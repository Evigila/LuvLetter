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
    public string Border { get; init; } = "#66FFFFFF";

    public string Background { get; init; } = "#80F5F5F5";

    public float BackgroundOpacity { get; init; } = 0.5f;

    public string Text { get; init; } = "#FFFFFFFF";

    public float TextOpacity { get; init; } = 1.0f;

    public string Caret { get; init; } = "#FFFFFFFF";
}

public sealed record InputBoxSizeOptions
{
    public const float DefaultHorizontalPadding = 10.0f;
    public const float DefaultVerticalPadding = 4.0f;
    public const float DefaultCaretWidth = 2.25f;

    public int Width { get; init; } = 560;

    public int Height { get; init; } = 32;

    public float CornerRadius { get; init; } = 8.0f;

    public float BorderThickness { get; init; } = 1.0f;

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
