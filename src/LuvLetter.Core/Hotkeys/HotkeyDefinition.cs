namespace LuvLetter.Core.Hotkeys;

public sealed record HotkeyDefinition(
    HotkeyModifierKeys Modifiers,
    int VirtualKey,
    string KeyName);
