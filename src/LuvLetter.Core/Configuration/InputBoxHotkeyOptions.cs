using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed record InputBoxHotkeyOptions
{
    public HotkeyDefinition Submit { get; init; } =
        new(HotkeyModifierKeys.None, 0x0D, "Enter");

    public HotkeyDefinition Cancel { get; init; } =
        new(HotkeyModifierKeys.None, 0x1B, "Escape");

    public HotkeyDefinition Backspace { get; init; } =
        new(HotkeyModifierKeys.None, 0x08, "Backspace");
}
