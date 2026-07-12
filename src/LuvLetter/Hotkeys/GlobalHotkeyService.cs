using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkControl = 0x11;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const int LlkhfExtended = 0x01;
    private const int KeyboardFlagsOffset = 8;

    private readonly Dispatcher dispatcher;
    private readonly LowLevelKeyboardProc hookCallback;
    private readonly DispatcherTimer deadlineTimer;
    private CtrlGestureStateMachine? stateMachine;
    private IntPtr hookHandle;
    private int disposalStarted;

    public GlobalHotkeyService()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        hookCallback = HandleLowLevelKeyboardEvent;
        deadlineTimer = new DispatcherTimer(DispatcherPriority.Send, dispatcher);
        deadlineTimer.Tick += HandleDeadlineTimerTick;
    }

    public event EventHandler? CommandRequested;

    public event EventHandler? FeatureWindowRequested;

    public void Start(ActivationGestureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InvokeOnDispatcher(() => StartCore(options));
    }

    public void Update(ActivationGestureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InvokeOnDispatcher(() => UpdateCore(options));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            DisposeCore();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            try
            {
                dispatcher.Invoke(DisposeCore);
            }
            catch (InvalidOperationException)
            {
                DisposeAfterDispatcherShutdown();
            }
            catch (TaskCanceledException)
            {
                DisposeAfterDispatcherShutdown();
            }
        }
        else
        {
            DisposeAfterDispatcherShutdown();
        }

        GC.SuppressFinalize(this);
    }

    private void StartCore(ActivationGestureOptions options)
    {
        ThrowIfDisposed();
        dispatcher.VerifyAccess();

        if (hookHandle != IntPtr.Zero)
        {
            throw new InvalidOperationException("The Ctrl gesture hook is already running.");
        }

        CtrlGestureStateMachine.ValidateOptions(options);
        var nextStateMachine = new CtrlGestureStateMachine(options);

        var moduleHandle = GetModuleHandle(null);
        if (moduleHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot resolve the LuvLetter module for the Ctrl gesture hook."
            );
        }

        var nextHookHandle = SetWindowsHookEx(
            WhKeyboardLl,
            hookCallback,
            moduleHandle,
            0
        );
        if (nextHookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot install the global Ctrl gesture hook."
            );
        }

        stateMachine = nextStateMachine;
        hookHandle = nextHookHandle;
    }

    private void UpdateCore(ActivationGestureOptions options)
    {
        ThrowIfDisposed();
        dispatcher.VerifyAccess();

        if (hookHandle == IntPtr.Zero || stateMachine is null)
        {
            // A failed initial hook installation must not make the settings window a
            // dead end. Applying the configuration is also the explicit retry path.
            StartCore(options);
            return;
        }

        stateMachine.Update(options);
        ScheduleNextDeadline();
    }

    private IntPtr HandleLowLevelKeyboardEvent(
        int code,
        IntPtr messagePointer,
        IntPtr keyboardData
    )
    {
        try
        {
            if (code >= 0 && Volatile.Read(ref disposalStarted) == 0 && stateMachine is not null)
            {
                ProcessKeyboardEvent(unchecked((int)messagePointer.ToInt64()), keyboardData);
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged hook boundary. Resetting the
            // in-progress gesture prevents a malformed event from triggering later.
            stateMachine?.Reset();
            deadlineTimer.Stop();
        }

        return CallNextHookEx(IntPtr.Zero, code, messagePointer, keyboardData);
    }

    private void ProcessKeyboardEvent(int message, IntPtr keyboardData)
    {
        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return;
        }

        var virtualKey = unchecked((uint)Marshal.ReadInt32(keyboardData));
        var timestampMs = Environment.TickCount64;
        var previousDeadline = stateMachine!.NextDeadlineTimestampMs;
        CtrlGestureAction action;

        if (TryGetControlSide(virtualKey, keyboardData, out var side))
        {
            action = isKeyDown
                ? stateMachine!.HandleControlDown(side, timestampMs)
                : stateMachine!.HandleControlUp(side, timestampMs);
        }
        else
        {
            stateMachine.HandleOtherKey(timestampMs);
            action = CtrlGestureAction.None;
        }

        if (previousDeadline != stateMachine.NextDeadlineTimestampMs)
        {
            ScheduleNextDeadline(timestampMs);
        }

        QueueGestureAction(action);
    }

    private void HandleDeadlineTimerTick(object? sender, EventArgs eventArgs)
    {
        deadlineTimer.Stop();

        if (Volatile.Read(ref disposalStarted) != 0 || stateMachine is null)
        {
            return;
        }

        var timestampMs = Environment.TickCount64;
        var action = stateMachine.HandleTimeout(timestampMs);
        ScheduleNextDeadline(timestampMs);
        QueueGestureAction(action);
    }

    private void ScheduleNextDeadline(long? timestampMs = null)
    {
        deadlineTimer.Stop();

        var deadlineAtMs = stateMachine?.NextDeadlineTimestampMs;
        if (!deadlineAtMs.HasValue || Volatile.Read(ref disposalStarted) != 0)
        {
            return;
        }

        var nowMs = timestampMs ?? Environment.TickCount64;
        var remainingMs = deadlineAtMs.Value > nowMs ? deadlineAtMs.Value - nowMs : 1;
        deadlineTimer.Interval = TimeSpan.FromMilliseconds(remainingMs);
        deadlineTimer.Start();
    }

    private void QueueGestureAction(CtrlGestureAction action)
    {
        if (action == CtrlGestureAction.None)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Volatile.Read(ref disposalStarted) != 0 || hookHandle == IntPtr.Zero)
                {
                    return;
                }

                if (action == CtrlGestureAction.CommandRequested)
                {
                    CommandRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    FeatureWindowRequested?.Invoke(this, EventArgs.Empty);
                }
            }),
            DispatcherPriority.Input
        );
    }

    private void DisposeCore()
    {
        dispatcher.VerifyAccess();
        deadlineTimer.Stop();
        deadlineTimer.Tick -= HandleDeadlineTimerTick;
        stateMachine?.Reset();
        stateMachine = null;

        var handle = Interlocked.Exchange(ref hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(handle);
        }
    }

    private void DisposeAfterDispatcherShutdown()
    {
        stateMachine = null;
        var handle = Interlocked.Exchange(ref hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(handle);
        }
    }

    private void InvokeOnDispatcher(Action action)
    {
        ThrowIfDisposed();

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("The owning WPF Dispatcher is shutting down.");
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
    }

    private static bool TryGetControlSide(
        uint virtualKey,
        IntPtr keyboardData,
        out ControlKeySide side
    )
    {
        switch (virtualKey)
        {
            case VkLeftControl:
                side = ControlKeySide.Left;
                return true;
            case VkRightControl:
                side = ControlKeySide.Right;
                return true;
            case VkControl:
                var flags = Marshal.ReadInt32(keyboardData, KeyboardFlagsOffset);
                side = (flags & LlkhfExtended) != 0
                    ? ControlKeySide.Right
                    : ControlKeySide.Left;
                return true;
            default:
                side = default;
                return false;
        }
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
