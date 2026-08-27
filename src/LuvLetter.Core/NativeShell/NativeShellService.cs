using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.Core.NativeShell;

public sealed class NativeShellService : INativeShell, INativeConfigurationSink, IDisposable
{
    private const int MaximumQuickActionCount = 4096;
    private const int MaximumCallbackTextLength = 1_048_576;
    private const int MaximumQuickActionLabelLength = 96;
    private const int MaximumMessageLength = 4096;
    private const int MaximumPendingNotifications = 128;

    private static readonly IReadOnlyDictionary<ulong, string> EmptyQuickActionMap =
        new Dictionary<ulong, string>();
    private static readonly ConcurrentBag<Delegate> FailedShutdownCallbackRoots = new();

    private readonly object operationSyncRoot = new();
    private readonly INativeShellApi nativeApi;
    private readonly QuickActionTokenRegistry quickActionTokenRegistry = new();
    private readonly BoundedCallbackDispatcher<CallbackNotification> notificationDispatcher;
    private readonly NativeFeatureActivatedCallback quickActionActivatedCallback;
    private readonly NativeInputSubmittedCallback inputSubmittedCallback;
    private IReadOnlyDictionary<ulong, string> activeQuickActionIds = EmptyQuickActionMap;
    private int disposed;

    public NativeShellService()
        : this(NativeShellApi.Instance)
    {
    }

    internal NativeShellService(INativeShellApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        this.nativeApi = nativeApi;
        nativeApi.EnsureCompatible();
        notificationDispatcher = new(MaximumPendingNotifications, RaiseNotification);
        quickActionActivatedCallback = HandleNativeQuickActionActivated;
        inputSubmittedCallback = HandleNativeInputSubmitted;

        try
        {
            ThrowIfFailed(
                nativeApi.SetInputSubmittedCallback(inputSubmittedCallback, IntPtr.Zero),
                "SetInputSubmittedCallback");
            ThrowIfFailed(
                nativeApi.SetFeatureActivatedCallback(quickActionActivatedCallback, IntPtr.Zero),
                "SetFeatureActivatedCallback");
        }
        catch
        {
            notificationDispatcher.Dispose();
            var callbacksDetached = TryUnregisterCallbacks();
            var shutdownSucceeded = TryShutdown();
            if (!callbacksDetached && !shutdownSucceeded)
            {
                FailedShutdownCallbackRoots.Add(inputSubmittedCallback);
                FailedShutdownCallbackRoots.Add(quickActionActivatedCallback);
            }
            throw;
        }
    }

    public event Action<InputSubmission>? InputSubmitted;

    public event Action<string>? QuickActionActivated;

    public event Action? QuickActionUnavailable;

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(inputBoxConfiguration);
        ArgumentNullException.ThrowIfNull(quickActionsConfiguration);

        var nativeConfiguration = NativeConfigurationMapper.Map(
            inputBoxConfiguration,
            quickActionsConfiguration,
            nativeApi.AbiVersion);
        var nativeInputConfig = nativeConfiguration.InputBox;
        var nativeFeatureConfig = nativeConfiguration.QuickActions;

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ApplyInputBoxConfig(in nativeInputConfig),
                "ApplyInputBoxConfig");
            ThrowIfFailed(
                nativeApi.ApplyFeatureWindowConfig(in nativeFeatureConfig),
                "ApplyFeatureWindowConfig");
        }
    }

    public void SynchronizeQuickActions(IReadOnlyList<QuickActionSnapshot> quickActions)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(quickActions);
        if (quickActions.Count > MaximumQuickActionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quickActions),
                $"At most {MaximumQuickActionCount} quick actions can be synchronized.");
        }

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();

            var tokenAssignment = quickActionTokenRegistry.Prepare(quickActions);
            var nativeItems = tokenAssignment.Items.Count == 0
                ? Array.Empty<NativeFeatureItem>()
                : new NativeFeatureItem[tokenAssignment.Items.Count];

            try
            {
                for (var index = 0; index < tokenAssignment.Items.Count; index++)
                {
                    var item = tokenAssignment.Items[index];
                    var label = NormalizeQuickActionLabel(item.DisplayName);
                    var labelPointer = Marshal.StringToHGlobalUni(label);
                    nativeItems[index] = new NativeFeatureItem
                    {
                        Token = item.Token,
                        Label = labelPointer,
                    };
                }

                var nextQuickActionIds = tokenAssignment.QuickActionIdsByToken;
                var previousQuickActionIds = Volatile.Read(ref activeQuickActionIds);
                var transitionQuickActionIds = QuickActionTokenRegistry.CreateTransitionMap(
                    previousQuickActionIds,
                    nextQuickActionIds);

                Volatile.Write(ref activeQuickActionIds, transitionQuickActionIds);
                try
                {
                    ThrowIfFailed(
                        nativeApi.SetFeatureItems(nativeItems, nativeItems.Length),
                        "SetFeatureItems");
                }
                catch
                {
                    Volatile.Write(ref activeQuickActionIds, previousQuickActionIds);
                    throw;
                }

                Volatile.Write(ref activeQuickActionIds, nextQuickActionIds);
                quickActionTokenRegistry.Commit(tokenAssignment);
            }
            finally
            {
                foreach (var nativeItem in nativeItems)
                {
                    if (nativeItem.Label != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(nativeItem.Label);
                    }
                }
            }
        }
    }

    public void ShowCommandInput()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.ShowInputBox(), "ShowInputBox");
        }
    }

    public void HideCommandInput()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.HideInputBox(), "HideInputBox");
        }
    }

    public void ToggleCommandInput()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.ToggleInputBox(), "ToggleInputBox");
        }
    }

    public void ShowQuickActions()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ShowFeatureWindow(),
                "ShowQuickActions");
        }
    }

    public void HideQuickActions()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.HideFeatureWindow(),
                "HideQuickActions");
        }
    }

    public void ToggleQuickActions()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ToggleFeatureWindow(),
                "ToggleQuickActions");
        }
    }

    public void EnqueueMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalized = NormalizeMessage(message);
        if (normalized.Length == 0)
        {
            return;
        }

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.EnqueueMessage(normalized, normalized.Length),
                "EnqueueMessage");
        }
    }

    public void ToggleMessageQueue()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.ToggleMessageQueue(), "ToggleMessageQueue");
        }
    }

    public void HideMessageQueue()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.HideMessageQueue(), "HideMessageQueue");
        }
    }

    public void HidePopups()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.HidePopups(), "HidePopups");
        }
    }

    public void Dispose()
    {
        lock (operationSyncRoot)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref activeQuickActionIds, EmptyQuickActionMap);
            notificationDispatcher.Dispose();

            var callbacksDetached = TryUnregisterCallbacks();
            var shutdownSucceeded = TryShutdown();
            if (!callbacksDetached && !shutdownSucceeded)
            {
                // A failed detach followed by a failed bounded shutdown could leave Native
                // holding these function pointers. Root them for the remaining process life.
                FailedShutdownCallbackRoots.Add(inputSubmittedCallback);
                FailedShutdownCallbackRoots.Add(quickActionActivatedCallback);
            }
        }
        GC.SuppressFinalize(this);
    }

    private void HandleNativeInputSubmitted(
        IntPtr text,
        int length,
        int inputMode,
        IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || text == IntPtr.Zero
                || length <= 0
                || length > MaximumCallbackTextLength
                || !Enum.IsDefined((InputMode)inputMode))
            {
                return;
            }

            var ownedText = Marshal.PtrToStringUni(text, length);
            if (!string.IsNullOrWhiteSpace(ownedText))
            {
                QueueNotification(new CallbackNotification(
                    new InputSubmission(ownedText, (InputMode)inputMode),
                    CallbackNotificationKind.InputSubmitted));
            }
        }
        catch
        {
            // No managed exception may cross the native callback boundary.
        }
    }

    private void HandleNativeQuickActionActivated(ulong token, IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var quickActionIds = Volatile.Read(ref activeQuickActionIds);
            if (token == 0)
            {
                QueueNotification(new CallbackNotification(
                    string.Empty,
                    CallbackNotificationKind.QuickActionUnavailable));
                return;
            }

            if (quickActionIds.TryGetValue(token, out var quickActionId))
            {
                QueueNotification(new CallbackNotification(
                    quickActionId,
                    CallbackNotificationKind.QuickActionActivated));
            }
        }
        catch
        {
            // No managed exception may cross the native callback boundary.
        }
    }

    private void QueueNotification(CallbackNotification notification)
    {
        _ = notificationDispatcher.TryEnqueue(notification);
    }

    private void RaiseNotification(CallbackNotification notification)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (notification.Kind == CallbackNotificationKind.QuickActionUnavailable)
        {
            foreach (Action handler in QuickActionUnavailable?.GetInvocationList()
                .Cast<Action>() ?? Array.Empty<Action>())
            {
                try
                {
                    handler();
                }
                catch
                {
                    // One consumer cannot terminate delivery to other consumers.
                }
            }
            return;
        }

        if (notification.Kind == CallbackNotificationKind.InputSubmitted)
        {
            if (notification.Value is not InputSubmission submission)
            {
                return;
            }

            foreach (Action<InputSubmission> handler in InputSubmitted?.GetInvocationList()
                .Cast<Action<InputSubmission>>() ?? Array.Empty<Action<InputSubmission>>())
            {
                try
                {
                    handler(submission);
                }
                catch
                {
                    // One consumer cannot terminate delivery to other consumers.
                }
            }
            return;
        }

        if (notification.Value is not string value)
        {
            return;
        }

        foreach (Action<string> handler in QuickActionActivated?.GetInvocationList()
            .Cast<Action<string>>() ?? Array.Empty<Action<string>>())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // One consumer cannot terminate delivery to other consumers.
            }
        }
    }

    private bool TryUnregisterCallbacks()
    {
        var succeeded = true;
        try
        {
            succeeded &= nativeApi.SetInputSubmittedCallback(null, IntPtr.Zero) >= 0;
        }
        catch
        {
            succeeded = false;
        }

        try
        {
            succeeded &= nativeApi.SetFeatureActivatedCallback(null, IntPtr.Zero) >= 0;
        }
        catch
        {
            succeeded = false;
        }

        return succeeded;
    }

    private bool TryShutdown()
    {
        try
        {
            return nativeApi.ShutdownInputBox() >= 0;
        }
        catch
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0)
        {
            throw new ExternalException(
                $"Native operation '{operation}' failed with HRESULT 0x{result:X8}.",
                result);
        }
    }

    private static string NormalizeQuickActionLabel(string displayName)
    {
        var label = displayName.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return label.Length <= MaximumQuickActionLabelLength
            ? label
            : label[..MaximumQuickActionLabelLength];
    }

    private static string NormalizeMessage(string message)
    {
        var normalized = message.Trim();
        return normalized.Length <= MaximumMessageLength
            ? normalized
            : normalized[..MaximumMessageLength];
    }

    private enum CallbackNotificationKind
    {
        InputSubmitted,
        QuickActionActivated,
        QuickActionUnavailable,
    }

    private readonly record struct CallbackNotification(
        object Value,
        CallbackNotificationKind Kind);
}
