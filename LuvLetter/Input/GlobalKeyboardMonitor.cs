using System.Runtime.InteropServices;
using LuvLetter.Overlay.Services;

namespace LuvLetter.Input;

// 全局键盘监听：
// 安装 Win32 低级键盘钩子。在 CLI 打开后，接管输入、删除、提交、关闭等行为。把结果转交给 OverlayCliController，再由它同步到原生叠加层。
// 识别热键 Alt + Backspace。
public sealed class GlobalKeyboardMonitor : IDisposable
{
    // WH_KEYBOARD_LL：低级键盘钩子，能捕获全局范围内的键盘输入。
    private const int WhKeyboardLl = 13;

    // 普通按键按下。
    private const int WmKeyDown = 0x0100;

    // 普通按键抬起。
    private const int WmKeyUp = 0x0101;

    // 系统按键按下，常用于 Alt 相关组合键。
    private const int WmSysKeyDown = 0x0104;

    // 系统按键抬起。
    private const int WmSysKeyUp = 0x0105;

    // Backspace：删除字符；同时也是默认热键的一部分。
    private const int VkBack = 0x08;

    // Enter / Return：提交当前输入。
    private const int VkReturn = 0x0D;

    // Esc：关闭 CLI。
    private const int VkEscape = 0x1B;

    // Space：空格输入。
    private const int VkSpace = 0x20;

    // Shift：修饰键，用来判断是否需要大写或符号。
    private const int VkShift = 0x10;

    // 左 Shift。
    private const int VkLShift = 0xA0;

    // 右 Shift。
    private const int VkRShift = 0xA1;

    // 左 Alt（VK_MENU）：默认热键 Alt + Backspace 的前半部分。
    private const int VkLMenu = 0xA4;

    // 数字键 0。
    private const int Vk0 = 0x30;

    // 数字键 9，上界。
    private const int Vk9 = 0x39;

    // 字母键 A。
    private const int VkA = 0x41;

    // 字母键 Z，上界。
    private const int VkZ = 0x5A;

    // `;` / `:`。
    private const int VkOem1 = 0xBA;

    // `=` / `+`。
    private const int VkOemPlus = 0xBB;

    // `,` / `<`。
    private const int VkOemComma = 0xBC;

    // `-` / `_`。
    private const int VkOemMinus = 0xBD;

    // `.` / `>`。
    private const int VkOemPeriod = 0xBE;

    // `/` / `?`。
    private const int VkOem2 = 0xBF;

    // `` ` `` / `~`。
    private const int VkOem3 = 0xC0;

    // `[` / `{`。
    private const int VkOem4 = 0xDB;

    // `\` / `|`。
    private const int VkOem5 = 0xDC;

    // `]` / `}`。
    private const int VkOem6 = 0xDD;

    // `'` / `"`。
    private const int VkOem7 = 0xDE;

    // CLI 会话控制器：维护是否打开、输入缓冲、提交行为和输出回显。
    private readonly OverlayCliController cliController;

    // 低级键盘回调委托：必须保持引用，避免被 GC 回收后钩子失效。
    private readonly LowLevelKeyboardProc hookProcedure;

    // 全局键盘钩子句柄。
    private IntPtr hookHandle;

    // 左 Alt 当前是否按下，用于识别 Alt + Backspace。
    private bool leftAltPressed;

    // Shift 当前是否按下，用于大小写和符号转换。
    private bool shiftPressed;

    // 热键去抖标记，避免按住 Backspace 时重复切换 CLI。
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

        // 安装全局键盘钩子，开始监听系统范围内的按键。
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

        // 卸载钩子，停止监听。
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

        // 先更新修饰键状态，这样后面的热键和字符转换才能准确识别当前组合键。
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

        // 只处理按下事件，抬起事件直接放行给系统。
        if (!isKeyDown)
        {
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        // 默认热键：左 Alt + Backspace。
        // 响应：切换 CLI 面板的打开 / 关闭，并吞掉按键，避免影响前台应用。
        if (keyboardData.VirtualKeyCode == VkBack && leftAltPressed)
        {
            if (!hotkeyBackspacePressed)
            {
                hotkeyBackspacePressed = true;
                InvokeOnUiThread(() => cliController.Toggle());
            }

            return new IntPtr(1);
        }

        // CLI 未打开时，除热键外的输入都继续交给系统。
        if (!cliController.IsOpen)
        {
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        // CLI 打开后的支持行为：
        // Esc -> 关闭 CLI
        // Enter -> 提交当前输入
        // Backspace -> 删除一个字符
        // 可打印字符 -> 追加到输入缓冲
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

        // 其他按键不属于 CLI 的输入范围，继续交给系统和前台程序。
        return CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    // 只把常见可打印键转换成字符串，供 CLI 输入缓冲使用。
    // 当前支持：A-Z、0-9、空格，以及常见 US 键盘标点。
    private static bool TryTranslatePrintableKey(
        int virtualKeyCode,
        bool shiftPressed,
        out string text
    )
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

    // Win32 钩子回调不应直接操作 WPF UI，因此这里统一切回 Dispatcher。
    // 这样 Toggle / Close / Backspace / Submit / AppendText 都能安全地回到 UI 线程执行。
    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    // WH_KEYBOARD_LL 传回来的原始结构体。
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    // 安装全局键盘钩子。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(
        int hookId,
        LowLevelKeyboardProc hookProcedure,
        IntPtr moduleHandle,
        uint threadId
    );

    // 卸载全局键盘钩子。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    // 将按键继续传递给下一个钩子或系统。
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam
    );

    // 获取当前模块句柄，供安装全局钩子时使用。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
}
