namespace LuvLetter.Core.Hotkeys;

[Flags]
public enum HotkeyModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}
