namespace LuvLetter.Core.Hotkeys;

public sealed record HotkeyDefinition(
    HotkeyModifierKeys Modifiers,
    int VirtualKey,
    string KeyName)
{
    public static HotkeyDefinition Default { get; } =
        new(HotkeyModifierKeys.Alt, 0x70, "F1");

    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotkeyModifierKeys.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotkeyModifierKeys.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotkeyModifierKeys.Win))
            {
                parts.Add("Win");
            }

            parts.Add(string.IsNullOrWhiteSpace(KeyName) ? $"VK {VirtualKey}" : KeyName);
            return string.Join("+", parts);
        }
    }
}
