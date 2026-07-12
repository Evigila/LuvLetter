using System.Collections.Concurrent;
using System.Globalization;
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
    private readonly Dictionary<string, ulong> tokensByFeatureId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CallbackNotification> pendingNotifications = new();
    private readonly NativeFeatureActivatedCallback featureActivatedCallback;
    private readonly NativeInputSubmittedCallback inputSubmittedCallback;
    private IReadOnlyDictionary<ulong, string> activeFeatureIds = EmptyFeatureMap;
    private ulong nextFeatureToken = 1;
    private int pendingNotificationCount;
    private int notificationDrainScheduled;
    private int disposed;

    public InputBoxService()
    {
        NativeInputBoxApi.EnsureCompatible();
        featureActivatedCallback = HandleNativeFeatureActivated;
        inputSubmittedCallback = HandleNativeInputSubmitted;

        try
        {
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.SetInputSubmittedCallback(inputSubmittedCallback, IntPtr.Zero),
                "SetInputSubmittedCallback");
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.SetFeatureActivatedCallback(featureActivatedCallback, IntPtr.Zero),
                "SetFeatureActivatedCallback");
        }
        catch
        {
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

    public void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(inputBoxConfiguration);
        ArgumentNullException.ThrowIfNull(featureWindowConfiguration);

        var normalized = LuvLetterConfigurationStore.Normalize(
            LuvLetterConfiguration.Default with
            {
                InputBox = inputBoxConfiguration,
                FeatureWindow = featureWindowConfiguration,
            });

        var inputBox = normalized.InputBox;
        var nativeInputConfig = new NativeInputBoxConfig
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeInputBoxConfig>()),
            AbiVersion = NativeInputBoxApi.AbiVersion,
            Width = inputBox.Size.Width,
            Height = inputBox.Size.Height,
            CornerRadius = inputBox.Size.CornerRadius,
            BorderThickness = inputBox.Size.BorderThickness,
            FontSize = inputBox.Size.FontSize,
            HorizontalPadding = inputBox.Size.HorizontalPadding,
            VerticalPadding = inputBox.Size.VerticalPadding,
            CaretWidth = inputBox.Size.CaretWidth,
            PositionMode = (int)inputBox.Placement.Mode,
            OffsetX = inputBox.Placement.OffsetX,
            OffsetY = inputBox.Placement.OffsetY,
            BottomMargin = inputBox.Placement.BottomMargin,
            CustomX = inputBox.Placement.CustomX,
            CustomY = inputBox.Placement.CustomY,
            BorderColor = ParseArgb(inputBox.Colors.Border, 0x66FFFFFF),
            BackgroundColor = ApplyOpacity(
                ParseArgb(inputBox.Colors.Background, 0x38F5F5F5),
                inputBox.Colors.BackgroundOpacity),
            TextColor = ParseArgb(inputBox.Colors.Text, 0xFFFFFFFF),
            CaretColor = ParseArgb(inputBox.Colors.Caret, 0xFFFFFFFF),
            SubmitVirtualKey = inputBox.Hotkeys.Submit.VirtualKey,
            CancelVirtualKey = inputBox.Hotkeys.Cancel.VirtualKey,
            BackspaceVirtualKey = inputBox.Hotkeys.Backspace.VirtualKey,
            SubmitModifiers = (int)inputBox.Hotkeys.Submit.Modifiers,
            CancelModifiers = (int)inputBox.Hotkeys.Cancel.Modifiers,
            BackspaceModifiers = (int)inputBox.Hotkeys.Backspace.Modifiers,
        };

        var featureWindow = normalized.FeatureWindow;
        var nativeFeatureConfig = new NativeFeatureWindowConfig
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeFeatureWindowConfig>()),
            AbiVersion = NativeInputBoxApi.AbiVersion,
            ItemsPerPage = featureWindow.Layout.ItemsPerPage,
            CellSize = featureWindow.Layout.CellSize,
            Gap = featureWindow.Layout.Gap,
            CornerRadius = featureWindow.Layout.CornerRadius,
            BorderThickness = featureWindow.Layout.BorderThickness,
            FontSize = featureWindow.Layout.FontSize,
            BottomMargin = featureWindow.Layout.BottomMargin,
            OffsetX = featureWindow.Layout.OffsetX,
            OffsetY = featureWindow.Layout.OffsetY,
            BorderColor = ParseArgb(featureWindow.Colors.Border, 0x66FFFFFF),
            BackgroundColor = ApplyOpacity(
                ParseArgb(featureWindow.Colors.Background, 0x38F5F5F5),
                featureWindow.Colors.BackgroundOpacity),
            TextColor = ParseArgb(featureWindow.Colors.Text, 0xFFFFFFFF),
            AccentColor = ParseArgb(featureWindow.Colors.Accent, 0xFFFFFFFF),
            PreviousVirtualKey = featureWindow.Hotkeys.PreviousPage.VirtualKey,
            NextVirtualKey = featureWindow.Hotkeys.NextPage.VirtualKey,
            CancelVirtualKey = featureWindow.Hotkeys.Cancel.VirtualKey,
            FirstItemVirtualKey = featureWindow.Hotkeys.FirstItemVirtualKey,
            PreviousModifiers = (int)featureWindow.Hotkeys.PreviousPage.Modifiers,
            NextModifiers = (int)featureWindow.Hotkeys.NextPage.Modifiers,
            CancelModifiers = (int)featureWindow.Hotkeys.Cancel.Modifiers,
        };

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.ApplyInputBoxConfig(in nativeInputConfig),
                "ApplyInputBoxConfig");
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.ApplyFeatureWindowConfig(in nativeFeatureConfig),
                "ApplyFeatureWindowConfig");
        }
    }

    public void SynchronizeFeatures(IReadOnlyList<FeatureDefinition> features)
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

            var nativeItems = new NativeFeatureItem[features.Count];
            var allocatedLabels = new IntPtr[features.Count];
            var nextFeatureIds = new Dictionary<ulong, string>(features.Count);

            try
            {
                for (var index = 0; index < features.Count; index++)
                {
                    var feature = features[index]
                        ?? throw new ArgumentException("A feature snapshot cannot contain null.", nameof(features));
                    var token = GetOrCreateFeatureToken(feature.Id);
                    var label = NormalizeFeatureLabel(feature.DisplayName);
                    var labelPointer = Marshal.StringToHGlobalUni(label);
                    allocatedLabels[index] = labelPointer;
                    nativeItems[index] = new NativeFeatureItem
                    {
                        Token = token,
                        Label = labelPointer,
                    };
                    nextFeatureIds.Add(token, feature.Id);
                }

                var previousFeatureIds = Volatile.Read(ref activeFeatureIds);
                Volatile.Write(ref activeFeatureIds, nextFeatureIds);
                try
                {
                    NativeInputBoxApi.ThrowIfFailed(
                        NativeInputBoxApi.SetFeatureItems(nativeItems, nativeItems.Length),
                        "SetFeatureItems");
                }
                catch
                {
                    Volatile.Write(ref activeFeatureIds, previousFeatureIds);
                    throw;
                }
            }
            finally
            {
                foreach (var labelPointer in allocatedLabels)
                {
                    if (labelPointer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(labelPointer);
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
            NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.ShowInputBox(), "ShowInputBox");
        }
    }

    public void Hide()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.HideInputBox(), "HideInputBox");
        }
    }

    public void Toggle()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.ToggleInputBox(), "ToggleInputBox");
        }
    }

    public void ShowFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.ShowFeatureWindow(),
                "ShowFeatureWindow");
        }
    }

    public void HideFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.HideFeatureWindow(),
                "HideFeatureWindow");
        }
    }

    public void ToggleFeatureWindow()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            NativeInputBoxApi.ThrowIfFailed(
                NativeInputBoxApi.ToggleFeatureWindow(),
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
            while (pendingNotifications.TryDequeue(out _))
            {
                Interlocked.Decrement(ref pendingNotificationCount);
            }

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

    private ulong GetOrCreateFeatureToken(string featureId)
    {
        if (tokensByFeatureId.TryGetValue(featureId, out var token))
        {
            return token;
        }

        token = nextFeatureToken++;
        if (token == 0)
        {
            token = nextFeatureToken++;
        }

        tokensByFeatureId.Add(featureId, token);
        return token;
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
        if (Volatile.Read(ref disposed) != 0 || !TryReserveNotificationSlot())
        {
            return;
        }

        pendingNotifications.Enqueue(notification);
        ScheduleNotificationDrain();
    }

    private bool TryReserveNotificationSlot()
    {
        while (true)
        {
            var count = Volatile.Read(ref pendingNotificationCount);
            if (count >= MaximumPendingNotifications)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref pendingNotificationCount, count + 1, count) == count)
            {
                return true;
            }
        }
    }

    private void ScheduleNotificationDrain()
    {
        if (Interlocked.CompareExchange(ref notificationDrainScheduled, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static (InputBoxService service) => service.DrainNotifications(),
                this,
                preferLocal: false);
        }
    }

    private void DrainNotifications()
    {
        while (true)
        {
            while (pendingNotifications.TryDequeue(out var notification))
            {
                Interlocked.Decrement(ref pendingNotificationCount);
                RaiseNotification(notification);
            }

            Volatile.Write(ref notificationDrainScheduled, 0);
            if (pendingNotifications.IsEmpty
                || Interlocked.CompareExchange(ref notificationDrainScheduled, 1, 0) != 0)
            {
                return;
            }
        }
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
            succeeded &= NativeInputBoxApi.SetInputSubmittedCallback(null, IntPtr.Zero) >= 0;
        }
        catch
        {
            succeeded = false;
        }

        try
        {
            succeeded &= NativeInputBoxApi.SetFeatureActivatedCallback(null, IntPtr.Zero) >= 0;
        }
        catch
        {
            succeeded = false;
        }

        return succeeded;
    }

    private static bool TryShutdown()
    {
        try
        {
            return NativeInputBoxApi.ShutdownInputBox() >= 0;
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

    private static string NormalizeFeatureLabel(string displayName)
    {
        var label = displayName.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return label.Length <= MaximumFeatureLabelLength
            ? label
            : label[..MaximumFeatureLabelLength];
    }

    private static uint ParseArgb(string? value, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        return hex.Length == 8
            && uint.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : fallback;
    }

    private static uint ApplyOpacity(uint argb, float opacity)
    {
        var alpha = (uint)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f);
        return (argb & 0x00FFFFFF) | (alpha << 24);
    }

    private readonly record struct CallbackNotification(string Value, bool IsFeature);
}
