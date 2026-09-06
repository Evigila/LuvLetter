using System.Runtime.InteropServices;
using System.Text;

namespace ArkheideSystem;

internal sealed class WindowsClipboard : IClipboard
{
    private const uint MoveableMemory = 0x0002;
    private const uint UnicodeTextFormat = 13;
    private const int MaximumOpenAttempts = 5;

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < MaximumOpenAttempts; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    return WriteText(text);
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt + 1 < MaximumOpenAttempts)
            {
                Thread.Sleep(10);
            }
        }
        return false;
    }

    private static bool WriteText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var memory = GlobalAlloc(MoveableMemory, checked((nuint)bytes.Length));
        if (memory == 0)
        {
            return false;
        }

        var ownsMemory = true;
        try
        {
            var destination = GlobalLock(memory);
            if (destination == 0)
            {
                return false;
            }
            try
            {
                Marshal.Copy(bytes, 0, destination, bytes.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (!EmptyClipboard() || SetClipboardData(UnicodeTextFormat, memory) == 0)
            {
                return false;
            }

            ownsMemory = false;
            return true;
        }
        finally
        {
            if (ownsMemory)
            {
                GlobalFree(memory);
            }
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
