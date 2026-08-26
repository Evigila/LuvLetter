using System.Globalization;
using System.Windows.Input;
using LuvLetter.Core.Hotkeys;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace LuvLetter.View.Settings;

internal static class HotkeyCapture
{
    public static string Format(HotkeyDefinition hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        var parts = new List<string>();
        if (hotkey.Modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifierKeys.Win))
        {
            parts.Add("Win");
        }

        parts.Add(
            string.IsNullOrWhiteSpace(hotkey.KeyName)
                ? $"VK {hotkey.VirtualKey}"
                : hotkey.KeyName);
        return string.Join("+", parts);
    }

    public static Key GetEventKey(WpfKeyEventArgs eventArgs)
    {
        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        return key == Key.ImeProcessed ? eventArgs.ImeProcessedKey : key;
    }

    public static bool TryCreate(
        WpfKeyEventArgs eventArgs,
        out HotkeyDefinition hotkey,
        out string error
    )
    {
        hotkey = new HotkeyDefinition(HotkeyModifierKeys.None, 0, string.Empty);
        error = string.Empty;

        var key = GetEventKey(eventArgs);
        if (IsModifierKey(key))
        {
            error = "Press a non-modifier key.";
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = "This key is not supported.";
            return false;
        }

        hotkey = new HotkeyDefinition(GetCurrentModifiers(), virtualKey, NormalizeKeyName(key));
        return true;
    }

    private static HotkeyModifierKeys GetCurrentModifiers()
    {
        var keyboardModifiers = Keyboard.Modifiers;
        var modifiers = HotkeyModifierKeys.None;

        if (keyboardModifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifierKeys.Win;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftAlt
        or Key.RightAlt
        or Key.LeftCtrl
        or Key.RightCtrl
        or Key.LeftShift
        or Key.RightShift
        or Key.LWin
        or Key.RWin
        or Key.System;

    private static string NormalizeKeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return $"NumPad{(int)(key - Key.NumPad0)}";
        }

        return key switch
        {
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Space => "Space",
            _ => key.ToString(),
        };
    }
}
