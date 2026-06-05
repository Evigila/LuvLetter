using System.Runtime.InteropServices;
using LuvLetter.Overlay.Services;

namespace LuvLetter.Input;

// Watches the global keyboard stream and reserves a single system-wide hotkey:
// Left Alt + Backspace. When that hotkey is pressed we toggle the overlay CLI.
// Normal text entry is intentionally not handled here anymore, because IME,
// Chinese composition, clipboard shortcuts, selection, and word navigation are
// now owned by the focused WPF input host.
public sealed class GlobalKeyboardMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;

    // Standard key down/up messages emitted by the low-level keyboard hook.
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    // Virtual-key codes used by the configured hotkey:
    // VkLMenu     -> left Alt
    // VkBack      -> Backspace
    private const int VkBack = 0x08;
    private const int VkLMenu = 0xA4;

    private readonly OverlayCliController cliController;
    private readonly LowLevelKeyboardProc hookProcedure;

    private IntPtr hookHandle;
    private bool leftAltPressed;
    private bool hotkeyBackspacePressed;

    public GlobalKeyboardMonitor(OverlayCliController cliController)
    {
        this.cliController = cliController;
        hookProcedure = HandleKeyboardHook;
    }

    public void Start()
    {
        if (hookHandle != IntPtr.Zero)
        {
            return;
        }

        hookHandle = SetWindowsHookExW(WhKeyboardLl, hookProcedure, GetModuleHandleW(null), 0);
        if (hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to install global keyboard hook. Win32: {Marshal.GetLastWin32Error()}"
            );
        }
    }

    public void Stop()
    {
        if (hookHandle == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(hookHandle);
        hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr HandleKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        var keyboardData = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var message = unchecked((int)wParam.ToInt64());
        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;

        switch (keyboardData.VirtualKeyCode)
        {
            case VkLMenu:
                leftAltPressed = isKeyDown || (!isKeyUp && leftAltPressed);
                if (isKeyUp)
                {
                    leftAltPressed = false;
                    hotkeyBackspacePressed = false;
                }

                break;
            case VkBack:
                if (isKeyUp)
                {
                    hotkeyBackspacePressed = false;
                }

                break;
        }

        if (!isKeyDown)
        {
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        if (keyboardData.VirtualKeyCode == VkBack && leftAltPressed)
        {
            if (!hotkeyBackspacePressed)
            {
                hotkeyBackspacePressed = true;
                InvokeOnUiThread(() => cliController.Toggle());
            }

            return new IntPtr(1);
        }

        return CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(
        int hookId,
        LowLevelKeyboardProc hookProcedure,
        IntPtr moduleHandle,
        uint threadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
}
