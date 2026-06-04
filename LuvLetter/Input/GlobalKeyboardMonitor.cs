using System.Runtime.InteropServices;
using LuvLetter.Overlay.Services;

namespace LuvLetter.Input;

public sealed class GlobalKeyboardMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int VkBack = 0x08;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkShift = 0x10;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLMenu = 0xA4;
    private const int Vk0 = 0x30;
    private const int Vk9 = 0x39;
    private const int VkA = 0x41;
    private const int VkZ = 0x5A;
    private const int VkOem1 = 0xBA;
    private const int VkOemPlus = 0xBB;
    private const int VkOemComma = 0xBC;
    private const int VkOemMinus = 0xBD;
    private const int VkOemPeriod = 0xBE;
    private const int VkOem2 = 0xBF;
    private const int VkOem3 = 0xC0;
    private const int VkOem4 = 0xDB;
    private const int VkOem5 = 0xDC;
    private const int VkOem6 = 0xDD;
    private const int VkOem7 = 0xDE;

    private readonly OverlayCliController cliController;
    private readonly LowLevelKeyboardProc hookProcedure;

    private IntPtr hookHandle;
    private bool leftAltPressed;
    private bool shiftPressed;
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
                $"Failed to install global keyboard hook. Win32: {Marshal.GetLastWin32Error()}");
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
            case VkShift:
            case VkLShift:
            case VkRShift:
                shiftPressed = isKeyDown || (!isKeyUp && shiftPressed);
                if (isKeyUp)
                {
                    shiftPressed = false;
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

        if (!cliController.IsOpen)
        {
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        if (keyboardData.VirtualKeyCode == VkEscape)
        {
            InvokeOnUiThread(() => cliController.Close());
            return new IntPtr(1);
        }

        if (keyboardData.VirtualKeyCode == VkReturn)
        {
            InvokeOnUiThread(() => _ = cliController.SubmitAsync());
            return new IntPtr(1);
        }

        if (keyboardData.VirtualKeyCode == VkBack)
        {
            InvokeOnUiThread(() => cliController.Backspace());
            return new IntPtr(1);
        }

        if (TryTranslatePrintableKey(keyboardData.VirtualKeyCode, shiftPressed, out var text))
        {
            InvokeOnUiThread(() => cliController.AppendText(text));
            return new IntPtr(1);
        }

        return CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    private static bool TryTranslatePrintableKey(int virtualKeyCode, bool shiftPressed, out string text)
    {
        if (virtualKeyCode is >= VkA and <= VkZ)
        {
            var character = (char)virtualKeyCode;
            text = (shiftPressed ? character : char.ToLowerInvariant(character)).ToString();
            return true;
        }

        if (virtualKeyCode is >= Vk0 and <= Vk9)
        {
            const string normalDigits = "0123456789";
            const string shiftedDigits = ")!@#$%^&*(";
            var index = virtualKeyCode - Vk0;
            text = (shiftPressed ? shiftedDigits[index] : normalDigits[index]).ToString();
            return true;
        }

        text = virtualKeyCode switch
        {
            VkSpace => " ",
            VkOemMinus => shiftPressed ? "_" : "-",
            VkOemPlus => shiftPressed ? "+" : "=",
            VkOemComma => shiftPressed ? "<" : ",",
            VkOemPeriod => shiftPressed ? ">" : ".",
            VkOem2 => shiftPressed ? "?" : "/",
            VkOem1 => shiftPressed ? ":" : ";",
            VkOem7 => shiftPressed ? "\"" : "'",
            VkOem4 => shiftPressed ? "{" : "[",
            VkOem5 => shiftPressed ? "|" : "\\",
            VkOem6 => shiftPressed ? "}" : "]",
            VkOem3 => shiftPressed ? "~" : "`",
            _ => string.Empty,
        };

        return text.Length > 0;
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

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

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
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
