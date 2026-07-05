using System.Runtime.InteropServices;
using System.Windows.Interop;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Native;

namespace LuvLetter.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly IInputBoxService inputBoxService;
    private HwndSource? messageSource;
    private HotkeyDefinition? currentHotkey;

    public GlobalHotkeyService(IInputBoxService inputBoxService)
    {
        this.inputBoxService = inputBoxService;
    }

    public void Start(HotkeyDefinition hotkey)
    {
        EnsureMessageSource();
        Register(hotkey);
        currentHotkey = hotkey;
    }

    public bool TryUpdate(HotkeyDefinition hotkey, out string? error)
    {
        EnsureMessageSource();
        var previousHotkey = currentHotkey;
        Unregister();

        if (TryRegister(hotkey, out error))
        {
            currentHotkey = hotkey;
            return true;
        }

        if (previousHotkey is not null && TryRegister(previousHotkey, out _))
        {
            currentHotkey = previousHotkey;
        }

        return false;
    }

    public void Dispose()
    {
        Unregister();
        if (messageSource is not null)
        {
            messageSource.RemoveHook(HandleWindowMessage);
            messageSource.Dispose();
            messageSource = null;
        }
    }

    private void EnsureMessageSource()
    {
        if (messageSource is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("LuvLetter.HotkeySink")
        {
            Width = 0,
            Height = 0,
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        };

        messageSource = new HwndSource(parameters);
        messageSource.AddHook(HandleWindowMessage);
    }

    private void Register(HotkeyDefinition hotkey)
    {
        if (!TryRegister(hotkey, out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    private bool TryRegister(HotkeyDefinition hotkey, out string? error)
    {
        error = null;
        if (messageSource is null)
        {
            error = "Hotkey message source is not ready";
            return false;
        }

        var modifiers = ToNativeModifiers(hotkey.Modifiers) | ModNoRepeat;
        if (RegisterHotKey(messageSource.Handle, HotkeyId, modifiers, (uint)hotkey.VirtualKey))
        {
            return true;
        }

        error = $"Cannot register {hotkey.DisplayText}. Win32: {Marshal.GetLastWin32Error()}";
        return false;
    }

    private void Unregister()
    {
        if (messageSource is not null)
        {
            _ = UnregisterHotKey(messageSource.Handle, HotkeyId);
        }
    }

    private IntPtr HandleWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            _ = Task.Run(inputBoxService.Show);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(HotkeyModifierKeys modifiers)
    {
        uint nativeModifiers = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            nativeModifiers |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            nativeModifiers |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            nativeModifiers |= ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Win))
        {
            nativeModifiers |= ModWin;
        }

        return nativeModifiers;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr hwnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
