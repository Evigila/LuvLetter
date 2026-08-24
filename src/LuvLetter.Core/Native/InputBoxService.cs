using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;

namespace LuvLetter.Core.Native;

public sealed class InputBoxService : IInputBoxService
{
    private const int MaximumFeatureCount = 4096;
    private const int MaximumCallbackTextLength = 1_048_576;
    private const int MaximumFeatureLabelLength = 96;
    private const int MaximumPendingNotifications = 128;

    private static readonly IReadOnlyDictionary<ulong, string> EmptyFeatureMap =
        new Dictionary<ulong, string>();
    private static readonly ConcurrentBag<Delegate> FailedShutdownCallbackRoots = new();

    private readonly object operationSyncRoot = new();
    private readonly INativeInputBoxApi nativeApi;
    private readonly FeatureTokenRegistry featureTokenRegistry = new();
    private readonly BoundedCallbackDispatcher<CallbackNotification> notificationDispatcher;
    private readonly NativeFeatureActivatedCallback featureActivatedCallback;
    private readonly NativeInputSubmittedCallback inputSubmittedCallback;
    private IReadOnlyDictionary<ulong, string> activeFeatureIds = EmptyFeatureMap;
    private int disposed;

    public InputBoxService()
        : this(NativeInputBoxApiAdapter.Instance)
    {
    }

    internal InputBoxService(INativeInputBoxApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        this.nativeApi = nativeApi;
        nativeApi.EnsureCompatible();
        notificationDispatcher = new(MaximumPendingNotifications, RaiseNotification);
        featureActivatedCallback = HandleNativeFeatureActivated;
        inputSubmittedCallback = HandleNativeInputSubmitted;

        try
        {
            ThrowIfFailed(
                nativeApi.SetInputSubmittedCallback(inputSubmittedCallback, IntPtr.Zero),
                "SetInputSubmittedCallback");
            ThrowIfFailed(
                nativeApi.SetFeatureActivatedCallback(featureActivatedCallback, IntPtr.Zero),
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
                FailedShutdownCallbackRoots.Add(featureActivatedCallback);
            }
            throw;
        }
    }

    public event Action<string>? InputSubmitted;

    public event Action<string>? FeatureActivated;

    public long DroppedNotificationCount => notificationDispatcher.DroppedCount;

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(inputBoxConfiguration);
        ArgumentNullException.ThrowIfNull(featureWindowConfiguration);

        var nativeConfiguration = NativeConfigurationMapper.Map(
            inputBoxConfiguration,
            featureWindowConfiguration,
            nativeApi.AbiVersion);
        var nativeInputConfig = nativeConfiguration.InputBox;
        var nativeFeatureConfig = nativeConfiguration.FeatureWindow;

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

    public void SynchronizeFeatures(IReadOnlyList<FeatureItemSnapshot> features)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count > MaximumFeatureCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(features),
                $"At most {MaximumFeatureCount} features can be synchronized.");
        }

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();

            var tokenAssignment = featureTokenRegistry.Prepare(features);
            var nativeItems = tokenAssignment.Items.Count == 0
                ? Array.Empty<NativeFeatureItem>()
                : new NativeFeatureItem[tokenAssignment.Items.Count];

            try
            {
                for (var index = 0; index < tokenAssignment.Items.Count; index++)
                {
                    var item = tokenAssignment.Items[index];
                    var label = NormalizeFeatureLabel(item.DisplayName);
                    var labelPointer = Marshal.StringToHGlobalUni(label);
                    nativeItems[index] = new NativeFeatureItem
                    {
                        Token = item.Token,
                        Label = labelPointer,
                    };
                }

                var nextFeatureIds = tokenAssignment.FeatureIdsByToken;
                var previousFeatureIds = Volatile.Read(ref activeFeatureIds);
                var transitionFeatureIds = FeatureTokenRegistry.CreateTransitionMap(
                    previousFeatureIds,
                    nextFeatureIds);

                Volatile.Write(ref activeFeatureIds, transitionFeatureIds);
                try
                {
                    ThrowIfFailed(
                        nativeApi.SetFeatureItems(nativeItems, nativeItems.Length),
                        "SetFeatureItems");
                }
                catch
                {
                    Volatile.Write(ref activeFeatureIds, previousFeatureIds);
                    throw;
                }

                Volatile.Write(ref activeFeatureIds, nextFeatureIds);
                featureTokenRegistry.Commit(tokenAssignment);
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

    public void Show()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.ShowInputBox(), "ShowInputBox");
        }
    }

    public void Hide()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.HideInputBox(), "HideInputBox");
        }
    }

    public void Toggle()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.ToggleInputBox(), "ToggleInputBox");
        }
    }

    public void ShowFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ShowFeatureWindow(),
                "ShowFeatureWindow");
        }
    }

    public void HideFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.HideFeatureWindow(),
                "HideFeatureWindow");
        }
    }

    public void ToggleFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ToggleFeatureWindow(),
                "ToggleFeatureWindow");
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

            Volatile.Write(ref activeFeatureIds, EmptyFeatureMap);
            notificationDispatcher.Dispose();

            var callbacksDetached = TryUnregisterCallbacks();
            var shutdownSucceeded = TryShutdown();
            if (!callbacksDetached && !shutdownSucceeded)
            {
                // A failed detach followed by a failed bounded shutdown could leave Native
                // holding these function pointers. Root them for the remaining process life.
                FailedShutdownCallbackRoots.Add(inputSubmittedCallback);
                FailedShutdownCallbackRoots.Add(featureActivatedCallback);
            }
        }
        GC.SuppressFinalize(this);
    }

    private void HandleNativeInputSubmitted(IntPtr text, int length, IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || text == IntPtr.Zero
                || length <= 0
                || length > MaximumCallbackTextLength)
            {
                return;
            }

            var ownedText = Marshal.PtrToStringUni(text, length);
            if (!string.IsNullOrWhiteSpace(ownedText))
            {
                QueueNotification(new CallbackNotification(ownedText, IsFeature: false));
            }
        }
        catch
        {
            // No managed exception may cross the native callback boundary.
        }
    }

    private void HandleNativeFeatureActivated(ulong token, IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var featureIds = Volatile.Read(ref activeFeatureIds);
            if (featureIds.TryGetValue(token, out var featureId))
            {
                QueueNotification(new CallbackNotification(featureId, IsFeature: true));
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

        var handlers = notification.IsFeature ? FeatureActivated : InputSubmitted;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(notification.Value);
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

    private static string NormalizeFeatureLabel(string displayName)
    {
        var label = displayName.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return label.Length <= MaximumFeatureLabelLength
            ? label
            : label[..MaximumFeatureLabelLength];
    }

    private readonly record struct CallbackNotification(string Value, bool IsFeature);
}
