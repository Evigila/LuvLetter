using System.Runtime.InteropServices;
using System.Windows.Threading;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Platform.Activation;

public sealed class ActivationGestureService : IActivationGestureService, IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkControl = 0x11;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkMenu = 0x12;
    private const uint VkLeftMenu = 0xA4;
    private const uint VkRightMenu = 0xA5;
    private const uint VkEscape = 0x1B;
    private const uint VkF1 = 0x70;
    private const int VkShift = 0x10;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const int LlkhfExtended = 0x01;
    private const int KeyboardFlagsOffset = 8;

    private readonly Dispatcher dispatcher;
    private readonly LowLevelKeyboardHook keyboardHook;
    private readonly DispatcherTimer deadlineTimer;
    private CtrlGestureStateMachine? stateMachine;
    private int disposalStarted;
    private long activationGeneration;

    public ActivationGestureService()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        keyboardHook = new LowLevelKeyboardHook(
            HandleLowLevelKeyboardEvent,
            HandleHookCallbackFailure
        );
        deadlineTimer = new DispatcherTimer(DispatcherPriority.Send, dispatcher);
        deadlineTimer.Tick += HandleDeadlineTimerTick;
    }

    public event EventHandler? CommandInputRequested;

    public event EventHandler? PopupsDismissRequested;

    public event EventHandler? QuickActionsRequested;

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

    internal void CancelPendingGestures()
    {
        InvokeOnDispatcher(CancelPendingGesturesCore);
    }

    void IActivationGestureService.CancelPendingGestures() => CancelPendingGestures();

    public void Stop() => InvokeOnDispatcher(StopCore);

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

        if (keyboardHook.IsRunning)
        {
            throw new InvalidOperationException("The Ctrl gesture hook is already running.");
        }

        var nextStateMachine = new CtrlGestureStateMachine(options);
        keyboardHook.Start();

        stateMachine = nextStateMachine;
        Interlocked.Increment(ref activationGeneration);
    }

    private void UpdateCore(ActivationGestureOptions options)
    {
        ThrowIfDisposed();
        dispatcher.VerifyAccess();

        if (!keyboardHook.IsRunning || stateMachine is null)
        {
            // A failed initial hook installation must not make the settings window a
            // dead end. Applying the configuration is also the explicit retry path.
            StartCore(options);
            return;
        }

        stateMachine.Update(options);
        Interlocked.Increment(ref activationGeneration);
        ScheduleNextDeadline();
    }

    private void CancelPendingGesturesCore()
    {
        ThrowIfDisposed();
        dispatcher.VerifyAccess();

        stateMachine?.CancelPending();
        deadlineTimer.Stop();
        Interlocked.Increment(ref activationGeneration);
    }

    private void HandleLowLevelKeyboardEvent(int message, IntPtr keyboardData)
    {
        if (Volatile.Read(ref disposalStarted) == 0 && stateMachine is not null)
        {
            ProcessKeyboardEvent(message, keyboardData);
        }
    }

    private void HandleHookCallbackFailure()
    {
        // Resetting the in-progress gesture prevents a malformed event from
        // triggering an activation after callback processing has failed.
        stateMachine?.Reset();
        deadlineTimer.Stop();
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
        else if (TryGetAltSide(virtualKey, keyboardData, out side))
        {
            action = isKeyDown
                ? stateMachine.HandleAltDown(side)
                : stateMachine.HandleAltUp(side);
        }
        else if (virtualKey == VkF1)
        {
            action = isKeyDown
                ? stateMachine.HandleFunctionOneDown(HasAdditionalHotkeyModifier())
                : stateMachine.HandleFunctionOneUp();
        }
        else if (virtualKey == VkEscape)
        {
            action = isKeyDown
                ? stateMachine.HandleEscapeDown()
                : stateMachine.HandleEscapeUp();
        }
        else
        {
            stateMachine.HandleOtherKey();
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

        var queuedGeneration = Volatile.Read(ref activationGeneration);
        _ = dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (
                    Volatile.Read(ref disposalStarted) != 0
                    || !keyboardHook.IsRunning
                    || queuedGeneration != Volatile.Read(ref activationGeneration)
                )
                {
                    return;
                }

                if (action == CtrlGestureAction.CommandInputRequested)
                {
                    CommandInputRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (action == CtrlGestureAction.PopupsDismissRequested)
                {
                    PopupsDismissRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (action == CtrlGestureAction.QuickActionsRequested)
                {
                    QuickActionsRequested?.Invoke(this, EventArgs.Empty);
                }
            }),
            DispatcherPriority.Input
        );
    }

    private void DisposeCore()
    {
        dispatcher.VerifyAccess();
        StopCore();
        deadlineTimer.Tick -= HandleDeadlineTimerTick;
        keyboardHook.Dispose();
    }

    private void StopCore()
    {
        dispatcher.VerifyAccess();
        deadlineTimer.Stop();
        Interlocked.Increment(ref activationGeneration);
        stateMachine?.Reset();
        stateMachine = null;
        keyboardHook.Stop();
    }

    private void DisposeAfterDispatcherShutdown()
    {
        stateMachine = null;
        keyboardHook.Dispose();
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

    private static bool TryGetAltSide(
        uint virtualKey,
        IntPtr keyboardData,
        out ControlKeySide side
    )
    {
        switch (virtualKey)
        {
            case VkLeftMenu:
                side = ControlKeySide.Left;
                return true;
            case VkRightMenu:
                side = ControlKeySide.Right;
                return true;
            case VkMenu:
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

    private static bool HasAdditionalHotkeyModifier() =>
        IsAsyncKeyDown(VkShift)
        || IsAsyncKeyDown(VkLeftWindows)
        || IsAsyncKeyDown(VkRightWindows);

    private static bool IsAsyncKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
