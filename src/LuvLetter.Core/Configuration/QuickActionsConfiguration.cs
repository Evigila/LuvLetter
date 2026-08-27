using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed record QuickActionsConfiguration
{
    public QuickActionsLayoutOptions Layout { get; init; } = new();

    public QuickActionsColorOptions Colors { get; init; } = new();

    public QuickActionsHotkeyOptions Hotkeys { get; init; } = new();
}

public sealed record QuickActionsLayoutOptions
{
    public const int MaximumItemsPerPage = 7;

    public int ItemsPerPage { get; init; } = MaximumItemsPerPage;

    public float CellSize { get; init; } = 96.0f;

    public float Gap { get; init; } = 12.0f;

    public float CornerRadius { get; init; } = SurfaceStyleDefaults.CornerRadius;

    public float BorderThickness { get; init; } = SurfaceStyleDefaults.BorderThickness;

    public float FontSize { get; init; } = 16.0f;

    public int BottomMargin { get; init; } = 60;

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }
}

public sealed record QuickActionsColorOptions
{
    public string Border { get; init; } = SurfaceStyleDefaults.Border;

    public string Background { get; init; } = SurfaceStyleDefaults.Background;

    public float BackgroundOpacity { get; init; } = SurfaceStyleDefaults.BackgroundOpacity;

    public string Text { get; init; } = SurfaceStyleDefaults.Content;

    public float TextOpacity { get; init; } = SurfaceStyleDefaults.ContentOpacity;

    public string Accent { get; init; } = SurfaceStyleDefaults.Content;
}

public sealed record QuickActionsHotkeyOptions
{
    public HotkeyDefinition PreviousPage { get; init; } =
        new(HotkeyModifierKeys.None, 0xBD, "-");

    public HotkeyDefinition NextPage { get; init; } =
        new(HotkeyModifierKeys.None, 0xBB, "=");

    public HotkeyDefinition Cancel { get; init; } =
        new(HotkeyModifierKeys.None, 0x1B, "Escape");

    public int FirstItemVirtualKey { get; init; } = 0x31;
}
