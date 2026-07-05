using System.Windows;
using System.Windows.Input;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Hotkeys;

namespace LuvLetter;

public partial class MainWindow : Window
{
    private readonly HotkeyConfigurationStore hotkeyStore;
    private readonly GlobalHotkeyService hotkeyService;
    private HotkeyDefinition pendingHotkey;

    public MainWindow(
        HotkeyConfigurationStore hotkeyStore,
        GlobalHotkeyService hotkeyService)
    {
        this.hotkeyStore = hotkeyStore;
        this.hotkeyService = hotkeyService;
        pendingHotkey = hotkeyStore.Current;

        InitializeComponent();
        HotkeyTextBox.Text = pendingHotkey.DisplayText;
        SetStatus("Ready");
    }

    public void SetStatus(string text)
    {
        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Text = text;
        }
    }

    private void HotkeyTextBox_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        if (!TryCreateHotkey(eventArgs, out var hotkey, out var error))
        {
            SetStatus(error);
            return;
        }

        pendingHotkey = hotkey;
        HotkeyTextBox.Text = hotkey.DisplayText;
        SetStatus("Pending");
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!hotkeyService.TryUpdate(pendingHotkey, out var error))
        {
            SetStatus(error ?? "Cannot register hotkey");
            return;
        }

        hotkeyStore.Update(pendingHotkey);
        SetStatus("Applied");
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        pendingHotkey = HotkeyDefinition.Default;
        HotkeyTextBox.Text = pendingHotkey.DisplayText;
        SetStatus("Pending");
    }

    private static bool TryCreateHotkey(
        KeyEventArgs eventArgs,
        out HotkeyDefinition hotkey,
        out string error)
    {
        hotkey = HotkeyDefinition.Default;
        error = string.Empty;

        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        key = key == Key.ImeProcessed ? eventArgs.ImeProcessedKey : key;
        if (IsModifierKey(key))
        {
            error = "Press a non-modifier key";
            return false;
        }

        var modifiers = GetCurrentModifiers();
        if (modifiers == HotkeyModifierKeys.None)
        {
            error = "Use at least one modifier";
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = "Unsupported key";
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey, NormalizeKeyName(key));
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

    private static bool IsModifierKey(Key key)
    {
        return key is
            Key.LeftAlt or
            Key.RightAlt or
            Key.LeftCtrl or
            Key.RightCtrl or
            Key.LeftShift or
            Key.RightShift or
            Key.LWin or
            Key.RWin or
            Key.System;
    }

    private static string NormalizeKeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return $"NumPad{(int)(key - Key.NumPad0)}";
        }

        return key.ToString();
    }
}
