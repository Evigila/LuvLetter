using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LuvLetter.Core.Application;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.Core.NativeShell;

public sealed class NativeShellService : INativeShell, INativeConfigurationSink, IDisposable
{
    private const int MaximumQuickActionCount = 4096;
    private const int MaximumCandidateCount = InputCandidateOptions.MaximumCandidateCount;
    private const int MaximumCallbackTextLength = 1_048_576;
    private const int MaximumInputTextLength = 32768;
    private const int MaximumQuickActionLabelLength = 96;
    private const int MaximumCandidatePrimaryTextLength = InputCandidatePresentation.MaximumPrimaryTextLength;
    private const int MaximumCandidateSecondaryTextLength = InputCandidatePresentation.MaximumSecondaryTextLength;
    private const int MaximumCandidateIconSourceLength = InputCandidatePresentation.MaximumIconSourceLength;
    private const int MaximumMessageLength = 4096;
    private const int MaximumPendingNotifications = 128;

    private static readonly IReadOnlyDictionary<ulong, string> EmptyQuickActionMap =
        new Dictionary<ulong, string>();
    private static readonly ConcurrentBag<Delegate> FailedShutdownCallbackRoots = new();

    private readonly object operationSyncRoot = new();
    private readonly INativeShellApi nativeApi;
    private readonly QuickActionTokenRegistry quickActionTokenRegistry = new();
    private readonly BoundedCallbackDispatcher<CallbackNotification> notificationDispatcher;
    private readonly LatestCallbackDispatcher<InputChanged> inputChangedDispatcher;
    private readonly NativeFeatureActivatedCallback quickActionActivatedCallback;
    private readonly NativeInputSubmittedCallback inputSubmittedCallback;
    private readonly NativeInputChangedCallback inputChangedCallback;
    private readonly NativeCandidateActivatedCallback candidateActivatedCallback;
    private IReadOnlyDictionary<ulong, string> activeQuickActionIds = EmptyQuickActionMap;
    private long nextMessageActivityToken;
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
        inputChangedDispatcher = new(RaiseInputChanged);
        quickActionActivatedCallback = HandleNativeQuickActionActivated;
        inputSubmittedCallback = HandleNativeInputSubmitted;
        inputChangedCallback = HandleNativeInputChanged;
        candidateActivatedCallback = HandleNativeCandidateActivated;

        try
        {
            ThrowIfFailed(
                nativeApi.SetInputSubmittedCallback(inputSubmittedCallback, IntPtr.Zero),
                "SetInputSubmittedCallback");
            ThrowIfFailed(
                nativeApi.SetInputChangedCallback(inputChangedCallback, IntPtr.Zero),
                "SetInputChangedCallback");
            ThrowIfFailed(
                nativeApi.SetCandidateActivatedCallback(candidateActivatedCallback, IntPtr.Zero),
                "SetCandidateActivatedCallback");
            ThrowIfFailed(
                nativeApi.SetFeatureActivatedCallback(quickActionActivatedCallback, IntPtr.Zero),
                "SetFeatureActivatedCallback");
        }
        catch
        {
            notificationDispatcher.Dispose();
            inputChangedDispatcher.Dispose();
            var callbacksDetached = TryUnregisterCallbacks();
            var shutdownSucceeded = TryShutdown();
            if (!callbacksDetached && !shutdownSucceeded)
            {
                FailedShutdownCallbackRoots.Add(inputSubmittedCallback);
                FailedShutdownCallbackRoots.Add(inputChangedCallback);
                FailedShutdownCallbackRoots.Add(candidateActivatedCallback);
                FailedShutdownCallbackRoots.Add(quickActionActivatedCallback);
            }
            throw;
        }
    }

    public event Action<InputSubmission>? InputSubmitted;

    public event Action<InputChanged>? InputChanged;

    public event Action<CandidateActivated>? CandidateActivated;

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

    public InputCandidateSetResult SetInputCandidates(
        IReadOnlyList<InputCandidate> candidates,
        ulong revision)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > MaximumCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidates),
                $"At most {MaximumCandidateCount} input candidates can be synchronized.");
        }

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            if (candidates.Count == 0)
            {
                return ParseCandidateSetResult(
                    nativeApi.SetInputCandidates(Array.Empty<NativeInputCandidate>(), 0, revision));
            }

            Span<int> primaryTextLengths = stackalloc int[candidates.Count];
            Span<int> secondaryTextLengths = stackalloc int[candidates.Count];
            Span<int> iconSourceLengths = stackalloc int[candidates.Count];
            var packedTextLength = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Token == 0
                    || !Enum.IsDefined(candidate.Kind)
                    || !Enum.IsDefined(candidate.IconKind)
                    || candidate.Actions == CandidateActions.None
                    || (candidate.Actions & ~(CandidateActions.Open
                        | CandidateActions.Reveal
                        | CandidateActions.Complete
                        | CandidateActions.CopyPath)) != 0)
                {
                    throw new ArgumentException(
                        "Candidates must have a non-zero token, valid kind, icon, and actions.",
                        nameof(candidates));
                }

                primaryTextLengths[index] = GetPackedTextLength(
                    candidate.PrimaryText,
                    MaximumCandidatePrimaryTextLength);
                secondaryTextLengths[index] = GetPackedTextLength(
                    candidate.SecondaryText,
                    MaximumCandidateSecondaryTextLength);
                iconSourceLengths[index] = GetPackedTextLength(
                    candidate.IconSource,
                    MaximumCandidateIconSourceLength);
                packedTextLength = checked(
                    packedTextLength
                    + primaryTextLengths[index] + 1
                    + secondaryTextLengths[index] + 1
                    + (iconSourceLengths[index] == 0 ? 0 : iconSourceLengths[index] + 1));
            }

            var packedText = ArrayPool<char>.Shared.Rent(packedTextLength);
            NativeInputCandidate[] nativeItems;
            try
            {
                nativeItems = ArrayPool<NativeInputCandidate>.Shared.Rent(candidates.Count);
            }
            catch
            {
                ArrayPool<char>.Shared.Return(packedText);
                throw;
            }

            GCHandle packedTextHandle = default;
            try
            {
                packedTextHandle = GCHandle.Alloc(packedText, GCHandleType.Pinned);
                var packedTextPointer = packedTextHandle.AddrOfPinnedObject();
                var packedTextOffset = 0;
                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    nativeItems[index] = new NativeInputCandidate
                    {
                        Token = candidate.Token,
                        Kind = (int)candidate.Kind,
                        IconKind = (int)candidate.IconKind,
                        Actions = (int)candidate.Actions,
                        PrimaryText = PackText(
                            candidate.PrimaryText,
                            primaryTextLengths[index],
                            packedText,
                            packedTextPointer,
                            ref packedTextOffset),
                        SecondaryText = PackText(
                            candidate.SecondaryText,
                            secondaryTextLengths[index],
                            packedText,
                            packedTextPointer,
                            ref packedTextOffset),
                        IconSource = iconSourceLengths[index] == 0
                            ? IntPtr.Zero
                            : PackText(
                                candidate.IconSource,
                                iconSourceLengths[index],
                                packedText,
                                packedTextPointer,
                                ref packedTextOffset),
                    };
                }

                // The native host copies every pointed-to text and icon-source string before this synchronous
                // call returns, so the pooled buffer can be unpinned immediately afterward.
                return ParseCandidateSetResult(
                    nativeApi.SetInputCandidates(nativeItems, candidates.Count, revision));
            }
            finally
            {
                if (packedTextHandle.IsAllocated)
                {
                    packedTextHandle.Free();
                }

                ArrayPool<char>.Shared.Return(packedText);
                nativeItems.AsSpan(0, candidates.Count).Clear();
                ArrayPool<NativeInputCandidate>.Shared.Return(nativeItems);
            }
        }
    }

    public void ReplaceCommandInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumInputTextLength || text.IndexOf('\0') >= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                "Command input is too long or contains a null character.");
        }

        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.ReplaceInputBoxText(text, text.Length, (int)InputMode.Command),
                "ReplaceInputBoxText");
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

    public void DismissCommandInput()
    {
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(nativeApi.DismissInputBox(), "DismissInputBox");
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

    public IMessageActivity BeginMessageActivity(string message)
    {
        var normalized = NormalizeRequiredMessage(message, nameof(message));
        var token = NextMessageActivityToken();
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.BeginMessageActivity(token, normalized, normalized.Length),
                "BeginMessageActivity");
        }

        return new NativeMessageActivity(this, token);
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
            inputChangedDispatcher.Dispose();

            var callbacksDetached = TryUnregisterCallbacks();
            var shutdownSucceeded = TryShutdown();
            if (!callbacksDetached && !shutdownSucceeded)
            {
                // A failed detach followed by a failed bounded shutdown could leave Native
                // holding these function pointers. Root them for the remaining process life.
                FailedShutdownCallbackRoots.Add(inputSubmittedCallback);
                FailedShutdownCallbackRoots.Add(inputChangedCallback);
                FailedShutdownCallbackRoots.Add(candidateActivatedCallback);
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

    private void HandleNativeInputChanged(
        IntPtr text,
        int length,
        int inputMode,
        ulong revision,
        IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || length < 0
                || length > MaximumCallbackTextLength
                || (length > 0 && text == IntPtr.Zero)
                || !Enum.IsDefined((InputMode)inputMode))
            {
                return;
            }

            var ownedText = length == 0
                ? string.Empty
                : Marshal.PtrToStringUni(text, length) ?? string.Empty;
            inputChangedDispatcher.TryPublish(
                new InputChanged(ownedText, (InputMode)inputMode, revision));
        }
        catch
        {
            // No managed exception may cross the native callback boundary.
        }
    }

    private void HandleNativeCandidateActivated(ulong token, int action, IntPtr context)
    {
        _ = context;
        try
        {
            if (Volatile.Read(ref disposed) != 0
                || token == 0
                || !Enum.IsDefined((CandidateAction)action))
            {
                return;
            }

            QueueNotification(new CallbackNotification(
                new CandidateActivated(token, (CandidateAction)action),
                CallbackNotificationKind.CandidateActivated));
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

        if (notification.Kind == CallbackNotificationKind.CandidateActivated)
        {
            if (notification.Value is not CandidateActivated activation)
            {
                return;
            }

            foreach (Action<CandidateActivated> handler in CandidateActivated?.GetInvocationList()
                .Cast<Action<CandidateActivated>>() ?? Array.Empty<Action<CandidateActivated>>())
            {
                try
                {
                    handler(activation);
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

    private void RaiseInputChanged(InputChanged change)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        foreach (Action<InputChanged> handler in InputChanged?.GetInvocationList()
            .Cast<Action<InputChanged>>() ?? Array.Empty<Action<InputChanged>>())
        {
            try
            {
                handler(change);
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
            succeeded &= nativeApi.SetInputChangedCallback(null, IntPtr.Zero) >= 0;
        }
        catch
        {
            succeeded = false;
        }

        try
        {
            succeeded &= nativeApi.SetCandidateActivatedCallback(null, IntPtr.Zero) >= 0;
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

    private static int GetPackedTextLength(string? text, int maximumLength)
    {
        text ??= string.Empty;
        var inspectedLength = Math.Min(text.Length, maximumLength);
        var terminator = text.AsSpan(0, inspectedLength).IndexOf('\0');
        if (terminator >= 0)
        {
            return terminator;
        }
        if (inspectedLength > 0 && inspectedLength < text.Length
            && char.IsHighSurrogate(text[inspectedLength - 1])
            && char.IsLowSurrogate(text[inspectedLength]))
        {
            inspectedLength--;
        }
        return inspectedLength;
    }

    private static InputCandidateSetResult ParseCandidateSetResult(int result)
    {
        if (result == 0) return InputCandidateSetResult.Accepted;
        if (result == 1) return InputCandidateSetResult.Stale;
        ThrowIfFailed(result, "SetInputCandidates");
        throw new ExternalException(
            $"Native operation 'SetInputCandidates' returned unexpected HRESULT 0x{result:X8}.",
            result);
    }

    private static IntPtr PackText(
        string? text,
        int length,
        char[] destination,
        IntPtr destinationPointer,
        ref int destinationOffset)
    {
        text ??= string.Empty;
        var result = IntPtr.Add(destinationPointer, checked(destinationOffset * sizeof(char)));
        text.AsSpan(0, length).CopyTo(destination.AsSpan(destinationOffset, length));
        destinationOffset += length;
        destination[destinationOffset++] = '\0';
        return result;
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

    private static string NormalizeRequiredMessage(string message, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(message, parameterName);
        var normalized = NormalizeMessage(message);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A message activity requires non-empty text.", parameterName);
        }

        return normalized;
    }

    private ulong NextMessageActivityToken()
    {
        var token = unchecked((ulong)Interlocked.Increment(ref nextMessageActivityToken));
        return token == 0
            ? unchecked((ulong)Interlocked.Increment(ref nextMessageActivityToken))
            : token;
    }

    private void UpdateMessageActivity(ulong token, string message)
    {
        var normalized = NormalizeRequiredMessage(message, nameof(message));
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.UpdateMessageActivity(token, normalized, normalized.Length),
                "UpdateMessageActivity");
        }
    }

    private void CompleteMessageActivity(ulong token, string? finalMessage)
    {
        var normalized = finalMessage is null ? string.Empty : NormalizeMessage(finalMessage);
        lock (operationSyncRoot)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                nativeApi.CompleteMessageActivity(
                    token,
                    normalized.Length == 0 ? null : normalized,
                    normalized.Length),
                "CompleteMessageActivity");
        }
    }

    private sealed class NativeMessageActivity : IMessageActivity
    {
        private readonly object syncRoot = new();
        private NativeShellService? owner;
        private readonly ulong token;

        internal NativeMessageActivity(NativeShellService owner, ulong token)
        {
            this.owner = owner;
            this.token = token;
        }

        public void Update(string message)
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(owner is null, this);
                owner.UpdateMessageActivity(token, message);
            }
        }

        public void Complete(string? finalMessage = null)
        {
            lock (syncRoot)
            {
                if (owner is null)
                {
                    return;
                }

                owner.CompleteMessageActivity(token, finalMessage);
                owner = null;
            }
        }

        public void Dispose()
        {
            NativeShellService? activityOwner;
            lock (syncRoot)
            {
                activityOwner = owner;
                owner = null;
            }

            if (activityOwner is null)
            {
                return;
            }

            try
            {
                activityOwner.CompleteMessageActivity(token, null);
            }
            catch
            {
                // Dispose is best-effort; Host shutdown also destroys every activity.
            }
        }
    }

    private enum CallbackNotificationKind
    {
        InputSubmitted,
        CandidateActivated,
        QuickActionActivated,
        QuickActionUnavailable,
    }

    private readonly record struct CallbackNotification(
        object Value,
        CallbackNotificationKind Kind);
}
