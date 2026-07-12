using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed record FeatureWindowHotkeyOptions
{
    public HotkeyDefinition PreviousPage { get; init; } =
        new(HotkeyModifierKeys.None, 0xBD, "-");

    public HotkeyDefinition NextPage { get; init; } =
        new(HotkeyModifierKeys.None, 0xBB, "=");

    public HotkeyDefinition Cancel { get; init; } =
        new(HotkeyModifierKeys.None, 0x1B, "Escape");

    public int FirstItemVirtualKey { get; init; } = 0x31;
}
