using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LuvLetter.Hotkeys;

internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;

    private readonly Action<int, IntPtr> keyboardEventHandler;
    private readonly Action callbackFailureHandler;
    private readonly LowLevelKeyboardProc hookCallback;
    private IntPtr hookHandle;
    private int disposalStarted;

    public LowLevelKeyboardHook(
        Action<int, IntPtr> keyboardEventHandler,
        Action callbackFailureHandler
    )
    {
        this.keyboardEventHandler = keyboardEventHandler;
        this.callbackFailureHandler = callbackFailureHandler;
        hookCallback = HandleLowLevelKeyboardEvent;
    }

    public bool IsRunning => Volatile.Read(ref hookHandle) != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The Ctrl gesture hook is already running.");
        }

        var moduleHandle = GetModuleHandle(null);
        if (moduleHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot resolve the LuvLetter module for the Ctrl gesture hook."
            );
        }

        var nextHookHandle = SetWindowsHookEx(WhKeyboardLl, hookCallback, moduleHandle, 0);
        if (nextHookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot install the global Ctrl gesture hook."
            );
        }

        hookHandle = nextHookHandle;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0)
        {
            return;
        }

        var handle = Interlocked.Exchange(ref hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(handle);
        }

        GC.SuppressFinalize(this);
    }

    private IntPtr HandleLowLevelKeyboardEvent(
        int code,
        IntPtr messagePointer,
        IntPtr keyboardData
    )
    {
        try
        {
            if (code >= 0 && Volatile.Read(ref disposalStarted) == 0)
            {
                keyboardEventHandler(unchecked((int)messagePointer.ToInt64()), keyboardData);
            }
        }
        catch
        {
            // No managed exception may cross the unmanaged hook callback boundary.
            try
            {
                callbackFailureHandler();
            }
            catch
            {
                // Failure recovery is best-effort at this unmanaged boundary.
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, messagePointer, keyboardData);
    }

    private delegate IntPtr LowLevelKeyboardProc(
        int code,
        IntPtr messagePointer,
        IntPtr keyboardData
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr moduleHandle,
        uint threadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr messagePointer,
        IntPtr keyboardData
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
