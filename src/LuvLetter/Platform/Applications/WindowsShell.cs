using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using LuvLetter.Core.Application;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32.SafeHandles;

namespace LuvLetter.Platform.Applications;

/// <summary>Shell discovery and activation use separate message-pumping STA threads.</summary>
internal static class WindowsShell
{
    private static readonly Guid ShellWindowsClassId = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserServiceId = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterfaceId = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid PersistFolderInterfaceId = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    private const uint ActivateViewWithFocus = 2;
    private const uint FocusedVisibleKeyboardSelection = 0x1 | 0x4 | 0x8 | 0x10 | 0x40 | 0x400;
    private const uint RootAncestor = 2;
    private const int MaximumExplorerFocusAttempts = 4;
    private const int MaximumExplorerShellSnapshots = 8;
    private static readonly TimeSpan ExplorerFocusTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExplorerFocusStabilityDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ExplorerFocusFinalCleanupDelay = TimeSpan.FromMilliseconds(1200);

    private static readonly Lazy<ShellWorker> ShellMetadataDiscoveryWorker =
        new(() => new("LuvLetter Shell metadata discovery"));
    private static readonly Lazy<ShellWorker> GeneralDiscoveryWorker =
        new(() => new("LuvLetter application discovery"));
    private static readonly Lazy<ShellWorker> ActivationWorker = new(() => new("LuvLetter application activation"));
    private static readonly Lazy<ExplorerAutomationBroker> ExplorerAutomation = new(() => new());

    internal static Task<T> DiscoverAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        DiscoverAsync("legacy", operation, cancellationToken);

    internal static Task<T> DiscoverAsync<T>(string sourceId, Func<T> operation, CancellationToken cancellationToken)
    {
        var worker = sourceId is "start-menu:user" or "start-menu:common" or "apps-folder"
            ? ShellMetadataDiscoveryWorker
            : GeneralDiscoveryWorker;
        return worker.Value.RunAsync(operation, cancellationToken);
    }

    internal static Task<T> ActivateAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        ActivationWorker.Value.RunAsync(operation, cancellationToken);

    internal static ApplicationLaunchResult Execute(
        string target, string? arguments = null, string? workingDirectory = null, bool invokeIdList = false)
    {
        var information = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            // Wait for Shell dispatch and suppress its generic error dialog; report failures in LuvLetter.
            Mask = 0x00000100U | 0x00000400U | (invokeIdList ? 0x0000000CU : 0U),
            File = target,
            Parameters = string.IsNullOrEmpty(arguments) ? null : arguments,
            Directory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            Show = 1,
        };
        var succeeded = ShellExecuteExW(ref information);
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        if (information.Process != IntPtr.Zero) CloseHandle(information.Process);
        return succeeded
            ? new(true)
            : Failure(error);
    }

    internal static ApplicationLaunchResult Reveal(string fullPath)
    {
        var normalized = NormalizePath(fullPath);
        var parent = normalized is null ? null : Path.GetDirectoryName(normalized);
        if (normalized is null || string.IsNullOrEmpty(parent))
        {
            return new(false, "无法确定候选项所在的文件夹。");
        }

        var folderIdList = ILCreateFromPathW(parent);
        var itemIdList = ILCreateFromPathW(normalized);
        if (folderIdList == IntPtr.Zero || itemIdList == IntPtr.Zero)
        {
            if (folderIdList != IntPtr.Zero) ILFree(folderIdList);
            if (itemIdList != IntPtr.Zero) ILFree(itemIdList);
            return new(false, "Windows 无法解析候选项的文件系统位置。");
        }

        ExplorerSelectionFocusRequest? focusRequest = null;
        try
        {
            var childIdList = ILFindChild(folderIdList, itemIdList);
            if (childIdList == IntPtr.Zero)
            {
                return new(false, "Windows 无法定位候选项。");
            }

            // Subscribe before asking Explorer to navigate so a cold view cannot publish its
            // selection/focus events before the request is listening.
            focusRequest = ExplorerSelectionFocusRequest.TryArm(
                parent, normalized, Dispatcher.CurrentDispatcher);

            var result = SHOpenFolderAndSelectItems(folderIdList, 1, [childIdList], 0);
            if (result < 0)
            {
                focusRequest?.Cancel();
                return new(false, $"无法在文件夹中定位候选项：{Marshal.GetExceptionForHR(result)?.Message}");
            }

            // Shell navigation is asynchronous. An immediate snapshot handles an already-open
            // folder; later Explorer events handle a newly-created or replaced item view.
            focusRequest?.AcceptShellRequest();
            focusRequest = null;
            return new(true);
        }
        finally
        {
            focusRequest?.Cancel();
            ILFree(itemIdList);
            ILFree(folderIdList);
        }
    }

    private static bool TryPrepareExplorerSelection(
        ExplorerSelectionFocusRequest request,
        object windows,
        string parent,
        string target)
    {
        var folderIdList = ILCreateFromPathW(parent);
        var itemIdList = ILCreateFromPathW(target);
        if (folderIdList == IntPtr.Zero || itemIdList == IntPtr.Zero)
        {
            if (folderIdList != IntPtr.Zero) ILFree(folderIdList);
            if (itemIdList != IntPtr.Zero) ILFree(itemIdList);
            return false;
        }
        try
        {
            var childIdList = ILFindChild(folderIdList, itemIdList);
            if (childIdList == IntPtr.Zero) return false;
            var foreground = GetForegroundWindow();
            return foreground != IntPtr.Zero
                && TryPrepareExplorerSelection(
                    request, windows, folderIdList, childIdList, foreground);
        }
        finally
        {
            ILFree(itemIdList);
            ILFree(folderIdList);
        }
    }

    private static bool TryPrepareExplorerSelection(
        ExplorerSelectionFocusRequest request,
        object windows,
        IntPtr folderIdList,
        IntPtr childIdList,
        IntPtr foreground)
    {
        var count = Convert.ToInt32(((dynamic)windows).Count);
        for (var index = count - 1; index >= 0; index--)
        {
            object? window = null;
            object? browser = null;
            object? folder = null;
            IShellView? view = null;
            IntPtr currentFolderIdList = IntPtr.Zero;
            try
            {
                window = ((dynamic)windows).Item(index);
                if (window is null) continue;

                var serviceId = TopLevelBrowserServiceId;
                var browserId = ShellBrowserInterfaceId;
                if (((IShellServiceProvider)window).QueryService(
                    ref serviceId, ref browserId, out browser) < 0 || browser is null) continue;
                if (((IShellBrowser)browser).QueryActiveShellView(out view) < 0 || view is null) continue;
                if (view.GetWindow(out var viewWindow) < 0 || viewWindow == IntPtr.Zero) continue;

                var explorerWindow = GetAncestor(viewWindow, RootAncestor);
                if (explorerWindow == IntPtr.Zero) explorerWindow = viewWindow;
                if (explorerWindow != foreground) continue;

                // If the target folder is still navigating, observe only this Explorer window.
                // Its structure event will trigger another Shell snapshot without scanning on a timer.
                ExplorerAutomation.Value.ObserveExplorer(request, explorerWindow);

                var folderId = PersistFolderInterfaceId;
                if (((IFolderView)view).GetFolder(ref folderId, out folder) < 0 || folder is null) continue;
                if (((IPersistFolder2)folder).GetCurFolder(out currentFolderIdList) < 0
                    || currentFolderIdList == IntPtr.Zero
                    || !ILIsEqual(folderIdList, currentFolderIdList)) continue;

                if (view.SelectItem(childIdList, FocusedVisibleKeyboardSelection) < 0
                    || view.UIActivate(ActivateViewWithFocus) < 0
                    || view.SelectItem(childIdList, FocusedVisibleKeyboardSelection) < 0) continue;

                ExplorerAutomation.Value.BindView(request, explorerWindow, viewWindow);
                return true;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException
                or FormatException or OverflowException or RuntimeBinderException or InvalidComObjectException)
            {
                // This collection can include non-Explorer Shell windows and closing tabs.
            }
            finally
            {
                if (currentFolderIdList != IntPtr.Zero) Marshal.FreeCoTaskMem(currentFolderIdList);
                ReleaseCom(folder);
                ReleaseCom(view);
                ReleaseCom(browser);
                ReleaseCom(window);
            }
        }
        return false;
    }

    private static bool IsCurrentExplorerView(
        object windows,
        string parent,
        IntPtr expectedExplorerWindow,
        IntPtr expectedViewWindow)
    {
        if (GetForegroundWindow() != expectedExplorerWindow
            || !IsWindow(expectedExplorerWindow)
            || !IsWindowVisible(expectedViewWindow)) return false;

        var folderIdList = ILCreateFromPathW(parent);
        if (folderIdList == IntPtr.Zero) return false;
        try
        {
            var count = Convert.ToInt32(((dynamic)windows).Count);
            for (var index = count - 1; index >= 0; index--)
            {
                object? window = null;
                object? browser = null;
                object? folder = null;
                IShellView? view = null;
                IntPtr currentFolderIdList = IntPtr.Zero;
                try
                {
                    window = ((dynamic)windows).Item(index);
                    if (window is null) continue;

                    var serviceId = TopLevelBrowserServiceId;
                    var browserId = ShellBrowserInterfaceId;
                    if (((IShellServiceProvider)window).QueryService(
                        ref serviceId, ref browserId, out browser) < 0 || browser is null) continue;
                    if (((IShellBrowser)browser).QueryActiveShellView(out view) < 0 || view is null) continue;
                    if (view.GetWindow(out var viewWindow) < 0 || viewWindow != expectedViewWindow) continue;

                    var explorerWindow = GetAncestor(viewWindow, RootAncestor);
                    if (explorerWindow == IntPtr.Zero) explorerWindow = viewWindow;
                    if (explorerWindow != expectedExplorerWindow) continue;

                    var folderId = PersistFolderInterfaceId;
                    if (((IFolderView)view).GetFolder(ref folderId, out folder) < 0 || folder is null) continue;
                    return ((IPersistFolder2)folder).GetCurFolder(out currentFolderIdList) >= 0
                        && currentFolderIdList != IntPtr.Zero
                        && ILIsEqual(folderIdList, currentFolderIdList)
                        && GetForegroundWindow() == expectedExplorerWindow;
                }
                catch (Exception exception) when (exception is COMException or InvalidCastException
                    or FormatException or OverflowException or RuntimeBinderException or InvalidComObjectException)
                {
                    // Explorer can replace the active tab while this snapshot is being read.
                }
                finally
                {
                    if (currentFolderIdList != IntPtr.Zero) Marshal.FreeCoTaskMem(currentFolderIdList);
                    ReleaseCom(folder);
                    ReleaseCom(view);
                    ReleaseCom(browser);
                    ReleaseCom(window);
                }
            }
            return false;
        }
        finally
        {
            ILFree(folderIdList);
        }
    }

    internal static ApplicationLaunchResult ExecuteWithSearchPath(string executablePath,
        string searchPath, string? workingDirectory)
    {
        // Shell alias lookup can select a different executable from cwd/PATH before App Paths.
        // A private child environment preserves the registered search path without changing ours.
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
            ? searchPath : searchPath + ";" + inheritedPath;
        try
        {
            using var process = Process.Start(startInfo);
            // This direct CreateProcess path, unlike Shell activation, always creates a process on success.
            return process is not null ? new(true) : new(false, "Windows 未接受应用启动请求。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            // Shell elevation cannot receive this private environment; never retry with altered semantics.
            return new(false, "此应用需要提升权限且依赖专用搜索路径，请使用其原始快捷方式启动。");
        }
    }

    internal static ApplicationLaunchResult Failure(int error) => new(
        false,
        error == 1223 ? "已取消应用启动。" : $"无法打开应用：{new Win32Exception(error).Message}",
        error == 1223);

    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string? ResolveFilePath(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null) return null;
        try
        {
            using var handle = File.OpenHandle(normalized, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var value = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(handle, value, (uint)value.Capacity, 0);
            if (length > 0 && length < value.Capacity)
            {
                var final = value.ToString();
                if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + final[8..];
                if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) return final[4..];
                return final;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A protected packaged executable still has a useful logical path.
        }
        return normalized;
    }

    internal static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new COMException($"Cannot create {progId}.");

    internal static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private sealed class ShellWorker
    {
        private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ShellWorker(string name)
        {
            var thread = new Thread(() =>
            {
                var result = CoInitializeEx(IntPtr.Zero, 0x2 | 0x4);
                if (result < 0)
                {
                    ready.TrySetException(Marshal.GetExceptionForHR(result)!);
                    return;
                }
                try
                {
                    ready.TrySetResult(Dispatcher.CurrentDispatcher);
                    Dispatcher.Run();
                }
                finally
                {
                    CoUninitialize();
                }
            }) { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            var dispatcher = await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        }
    }

    private sealed class ExplorerSelectionFocusRequest
    {
        private readonly string parent;
        private readonly string target;
        private readonly object windows;
        private readonly Dispatcher dispatcher;
        private int accepted;
        private int shellSnapshotCount;
        private int shellSignalQueued;
        private int stopped;

        private ExplorerSelectionFocusRequest(
            string parent,
            string target,
            object windows,
            Dispatcher dispatcher)
        {
            this.parent = parent;
            this.target = target;
            this.windows = windows;
            this.dispatcher = dispatcher;
        }

        internal bool IsStopped => Volatile.Read(ref stopped) != 0;

        internal static ExplorerSelectionFocusRequest? TryArm(
            string parent,
            string target,
            Dispatcher dispatcher)
        {
            object? windows = null;
            ExplorerSelectionFocusRequest? request = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ShellWindowsClassId, throwOnError: true)!;
                windows = Activator.CreateInstance(type);
                if (windows is null) return null;

                request = new ExplorerSelectionFocusRequest(parent, target, windows, dispatcher);
                windows = null;
                ExplorerAutomation.Value.Arm(request);
                return request;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException
                or FormatException or OverflowException or RuntimeBinderException or InvalidComObjectException
                or InvalidOperationException or ArgumentException)
            {
                // The Shell selection remains useful when UI Automation is unavailable.
                request?.Finish();
                return null;
            }
            finally
            {
                ReleaseCom(windows);
            }
        }

        internal void AcceptShellRequest()
        {
            if (IsStopped) return;
            Volatile.Write(ref accepted, 1);
            ExplorerAutomation.Value.Accept(this);
        }

        internal void SignalFromAutomation()
        {
            if (IsStopped || Volatile.Read(ref accepted) == 0
                || Volatile.Read(ref shellSnapshotCount) >= MaximumExplorerShellSnapshots
                || Interlocked.Exchange(ref shellSignalQueued, 1) != 0) return;

            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ProcessShellSignal));
        }

        private void ProcessShellSignal()
        {
            Volatile.Write(ref shellSignalQueued, 0);
            if (IsStopped || Volatile.Read(ref accepted) == 0) return;
            if (Interlocked.Increment(ref shellSnapshotCount) > MaximumExplorerShellSnapshots) return;

            PrepareSelectionCore();
        }

        internal void PrepareFinalSelection()
        {
            if (IsStopped || Volatile.Read(ref accepted) == 0) return;
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (IsStopped || Volatile.Read(ref accepted) == 0) return;
                    PrepareSelectionCore();
                    ExplorerAutomation.Value.FinalPreparationCompleted(this);
                }));
            }
            catch (InvalidOperationException)
            {
                ExplorerAutomation.Value.FinalPreparationCompleted(this);
            }
        }

        internal void ValidateExplorerView(
            IntPtr explorerWindow,
            IntPtr viewWindow,
            long bindingVersion,
            long validationVersion)
        {
            if (IsStopped || Volatile.Read(ref accepted) == 0) return;
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (IsStopped || Volatile.Read(ref accepted) == 0) return;
                    var isCurrent = false;
                    try
                    {
                        isCurrent = WindowsShell.IsCurrentExplorerView(
                            windows, parent, explorerWindow, viewWindow);
                    }
                    catch (Exception exception) when (exception is COMException or InvalidCastException
                        or FormatException or OverflowException or RuntimeBinderException
                        or InvalidComObjectException)
                    {
                        // Treat a view being replaced during validation as no longer current.
                    }
                    ExplorerAutomation.Value.ViewValidationCompleted(
                        this, bindingVersion, validationVersion, isCurrent);
                }));
            }
            catch (InvalidOperationException)
            {
                ExplorerAutomation.Value.ViewValidationCompleted(
                    this, bindingVersion, validationVersion, isCurrent: false);
            }
        }

        private bool PrepareSelectionCore()
        {
            if (IsStopped || Volatile.Read(ref accepted) == 0) return false;

            try
            {
                return TryPrepareExplorerSelection(this, windows, parent, target);
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException
                or FormatException or OverflowException or RuntimeBinderException or InvalidComObjectException)
            {
                // Explorer can replace a window or tab while its new view is loading. Its next
                // automation event will resolve the current view without a periodic retry.
                return false;
            }
        }

        internal void Cancel()
        {
            if (IsStopped) return;
            try
            {
                ExplorerAutomation.Value.Cancel(this);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                Finish();
            }
        }

        internal void Finish()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            if (dispatcher.CheckAccess())
            {
                ReleaseCom(windows);
                return;
            }

            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => ReleaseCom(windows)));
        }
    }

    /// <summary>
    /// Coordinates short-lived Explorer focus sessions on a windowless MTA. The broker reacts
    /// to UI Automation events and uses one-shot timers only for stability and cleanup.
    /// </summary>
    private sealed class ExplorerAutomationBroker
    {
        private readonly BlockingCollection<Action> operations = new();
        private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile AutomationSession? activeSession;

        internal ExplorerAutomationBroker()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "LuvLetter Explorer automation",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        internal void Arm(ExplorerSelectionFocusRequest request) => Invoke(() => ArmCore(request));

        internal void Accept(ExplorerSelectionFocusRequest request) => Post(() =>
        {
            if (!IsActive(request)) return;
            activeSession!.TimeoutTimer!.Change(
                ExplorerFocusTimeout,
                Timeout.InfiniteTimeSpan);
            request.SignalFromAutomation();
        });

        internal void BindView(
            ExplorerSelectionFocusRequest request,
            IntPtr explorerWindow,
            IntPtr viewWindow) =>
            Post(() => BindViewCore(request, explorerWindow, viewWindow));

        internal void ObserveExplorer(
            ExplorerSelectionFocusRequest request,
            IntPtr explorerWindow) =>
            Post(() => ObserveExplorerCore(request, explorerWindow));

        internal void ViewValidationCompleted(
            ExplorerSelectionFocusRequest request,
            long bindingVersion,
            long validationVersion,
            bool isCurrent) =>
            Post(() => ViewValidationCompletedCore(
                request, bindingVersion, validationVersion, isCurrent));

        internal void FinalPreparationCompleted(ExplorerSelectionFocusRequest request) =>
            Post(() => FinalPreparationCompletedCore(request));

        internal void Cancel(ExplorerSelectionFocusRequest request) =>
            Post(() => CompleteIfActive(request));

        private void Run()
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 0);
            if (initialized < 0)
            {
                ready.TrySetException(Marshal.GetExceptionForHR(initialized)!);
                return;
            }

            ready.TrySetResult(true);
            try
            {
                foreach (var operation in operations.GetConsumingEnumerable())
                {
                    try
                    {
                        operation();
                    }
                    catch (Exception exception) when (exception is COMException
                        or ElementNotAvailableException or InvalidOperationException or ArgumentException)
                    {
                        if (activeSession is { } session) CompleteCore(session);
                    }
                }
            }
            finally
            {
                if (activeSession is { } session) CompleteCore(session);
                CoUninitialize();
            }
        }

        private void ArmCore(ExplorerSelectionFocusRequest request)
        {
            if (activeSession is { } previous) CompleteCore(previous);

            var session = new AutomationSession(request);
            activeSession = session;
            try
            {
                session.TimeoutTimer = new System.Threading.Timer(
                    _ => Post(() => HandleHardTimeout(request)));
                session.StabilityTimer = new System.Threading.Timer(
                    _ => Post(() => VerifyStableFocus(session)));
                session.SelectedKeyboardItemCondition = new AndCondition(
                    new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true),
                    new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
                session.SelectionContainerCondition = new PropertyCondition(
                    AutomationElement.IsSelectionPatternAvailableProperty,
                    true);
                session.DesktopRoot = AutomationElement.RootElement;
                session.FocusChangedHandler = (_, _) =>
                    QueueEventSignal(session, AutomationSignal.Focus);
                session.AutomationEventHandler = (_, eventArguments) =>
                    QueueEventSignal(
                        session,
                        eventArguments.EventId == WindowPattern.WindowOpenedEvent
                            ? AutomationSignal.Window
                            : AutomationSignal.Selection);
                Automation.AddAutomationFocusChangedEventHandler(session.FocusChangedHandler);
                session.FocusHandlerRegistered = true;
                Automation.AddAutomationEventHandler(
                    SelectionItemPattern.ElementSelectedEvent,
                    session.DesktopRoot,
                    TreeScope.Descendants,
                    session.AutomationEventHandler);
                session.SelectionHandlerRegistered = true;
                Automation.AddAutomationEventHandler(
                    WindowPattern.WindowOpenedEvent,
                    session.DesktopRoot,
                    TreeScope.Descendants,
                    session.AutomationEventHandler);
                session.WindowHandlerRegistered = true;
            }
            catch
            {
                CompleteCore(session);
                throw;
            }
        }

        private void ObserveExplorerCore(
            ExplorerSelectionFocusRequest request,
            IntPtr explorerWindow)
        {
            if (!IsActive(request) || GetForegroundWindow() != explorerWindow
                || !IsWindow(explorerWindow) || !IsWindowVisible(explorerWindow)) return;

            var session = activeSession!;
            if (session.ViewRoot is not null && session.ExplorerWindow == explorerWindow) return;
            if (session.ObservedRoot is not null
                && session.ObservedExplorerWindow == explorerWindow) return;

            RemoveObservedExplorerHandler(session);
            try
            {
                var observedRoot = AutomationElement.FromHandle(explorerWindow);
                if (observedRoot.Current.IsOffscreen) return;

                var observationVersion = Interlocked.Increment(ref session.ObservationVersion);
                StructureChangedEventHandler handler = (_, _) =>
                {
                    if (ReferenceEquals(activeSession, session)
                        && Interlocked.Read(ref session.ObservationVersion) == observationVersion)
                    {
                        QueueEventSignal(session, AutomationSignal.Structure);
                    }
                };
                session.ObservedExplorerWindow = explorerWindow;
                session.ObservedRoot = observedRoot;
                session.ObservedStructureHandler = handler;
                session.ObservedStructureHandlerRegistered = true;
                Automation.AddStructureChangedEventHandler(
                    observedRoot,
                    TreeScope.Subtree,
                    handler);
            }
            catch (Exception exception) when (exception is COMException
                or ElementNotAvailableException or InvalidOperationException or ArgumentException)
            {
                RemoveObservedExplorerHandler(session);
            }
        }

        private void BindViewCore(
            ExplorerSelectionFocusRequest request,
            IntPtr explorerWindow,
            IntPtr viewWindow)
        {
            if (!IsActive(request) || GetForegroundWindow() != explorerWindow
                || !IsWindow(explorerWindow) || !IsWindowVisible(viewWindow)) return;

            var session = activeSession!;
            if (session.ViewWindow != viewWindow || session.ViewRoot is null)
            {
                RemoveViewHandler(session);
                RemoveObservedExplorerHandler(session);
                session.ExplorerWindow = explorerWindow;
                session.ViewWindow = viewWindow;
                try
                {
                    session.ViewRoot = AutomationElement.FromHandle(viewWindow);
                    if (session.ViewRoot.Current.IsOffscreen)
                    {
                        RemoveViewHandler(session);
                        ObserveExplorerCore(request, explorerWindow);
                        return;
                    }

                    var bindingVersion = Interlocked.Increment(ref session.BindingVersion);
                    StructureChangedEventHandler handler = (_, _) =>
                    {
                        if (ReferenceEquals(activeSession, session)
                            && Interlocked.Read(ref session.BindingVersion) == bindingVersion)
                        {
                            QueueEventSignal(session, AutomationSignal.Structure);
                        }
                    };
                    session.ViewStructureHandler = handler;
                    session.StructureHandlerRegistered = true;
                    Automation.AddStructureChangedEventHandler(
                        session.ViewRoot,
                        TreeScope.Subtree,
                        handler);
                }
                catch (Exception exception) when (exception is COMException
                    or ElementNotAvailableException or InvalidOperationException or ArgumentException)
                {
                    // A new Explorer tab can expose its HWND before its UIA provider is ready.
                    // Keep the request armed; a later structure/focus event will bind the view.
                    RemoveViewHandler(session);
                    ObserveExplorerCore(request, explorerWindow);
                    return;
                }
            }

            TryFocusSelectedItem(session);
        }

        private void QueueEventSignal(AutomationSession sourceSession, AutomationSignal signal)
        {
            if (!ReferenceEquals(activeSession, sourceSession)) return;
            Interlocked.Or(ref sourceSession.PendingEventSignals, (int)signal);
            if (Interlocked.Exchange(ref sourceSession.EventSignalQueued, 1) != 0) return;
            Post(() =>
            {
                Volatile.Write(ref sourceSession.EventSignalQueued, 0);
                var signals = (AutomationSignal)Interlocked.Exchange(
                    ref sourceSession.PendingEventSignals, 0);
                var session = activeSession;
                if (!ReferenceEquals(session, sourceSession)) return;

                if ((signals & AutomationSignal.Structure) != 0)
                {
                    session.SelectionContainer = null;
                    session.SelectedItem = null;
                }
                else if ((signals & AutomationSignal.Selection) != 0)
                {
                    session.ValidationVersion++;
                    session.ValidationPending = false;
                    session.SelectedItem = null;
                }

                if (session.ViewRoot is null) session.Request.SignalFromAutomation();
                else TryFocusSelectedItem(
                    session,
                    allowTreeLookup: (signals
                        & (AutomationSignal.Selection | AutomationSignal.Structure | AutomationSignal.Window)) != 0);
            });
        }

        private void TryFocusSelectedItem(AutomationSession session, bool allowTreeLookup = true)
        {
            if (!ReferenceEquals(activeSession, session) || session.ViewRoot is null) return;
            if (GetForegroundWindow() != session.ExplorerWindow || !IsWindow(session.ExplorerWindow)
                || !IsWindowVisible(session.ViewWindow))
            {
                CompleteCore(session);
                return;
            }

            try
            {
                if (session.ViewRoot.Current.IsOffscreen) return;
                var selected = GetSelectedItem(session, allowTreeLookup);
                if (selected is null) return;

                session.SelectedItem = selected;
                if (!selected.Current.HasKeyboardFocus)
                {
                    RequestViewValidation(session);
                    return;
                }

                ScheduleStabilityCheck(session);
            }
            catch (ElementNotAvailableException)
            {
                RemoveViewHandler(session);
                ObserveExplorerCore(session.Request, session.ExplorerWindow);
                session.Request.SignalFromAutomation();
            }
            catch (COMException)
            {
                RemoveViewHandler(session);
                ObserveExplorerCore(session.Request, session.ExplorerWindow);
                session.Request.SignalFromAutomation();
            }
            catch (InvalidOperationException)
            {
                // The selected item can be between virtualized and realized states. A later
                // selection or structure event will retry this exact operation.
            }
        }

        private void RequestViewValidation(AutomationSession session)
        {
            if (session.ValidationPending
                || session.FocusAttempts >= MaximumExplorerFocusAttempts) return;

            session.ValidationPending = true;
            var validationVersion = ++session.ValidationVersion;
            session.Request.ValidateExplorerView(
                session.ExplorerWindow,
                session.ViewWindow,
                session.BindingVersion,
                validationVersion);
        }

        private void ViewValidationCompletedCore(
            ExplorerSelectionFocusRequest request,
            long bindingVersion,
            long validationVersion,
            bool isCurrent)
        {
            if (!IsActive(request)) return;
            var session = activeSession!;
            if (session.BindingVersion != bindingVersion
                || session.ValidationVersion != validationVersion) return;

            session.ValidationPending = false;
            if (!isCurrent)
            {
                CompleteCore(session);
                return;
            }

            FocusValidatedSelectedItem(session);
        }

        private void FocusValidatedSelectedItem(AutomationSession session)
        {
            if (!ReferenceEquals(activeSession, session) || session.ViewRoot is null) return;
            if (GetForegroundWindow() != session.ExplorerWindow
                || !IsWindow(session.ExplorerWindow)
                || !IsWindowVisible(session.ViewWindow))
            {
                CompleteCore(session);
                return;
            }

            try
            {
                if (session.ViewRoot.Current.IsOffscreen) return;
                var selected = GetSelectedItem(session, allowTreeLookup: true);
                if (selected is null) return;

                session.SelectedItem = selected;
                if (!selected.Current.HasKeyboardFocus)
                {
                    if (session.FocusAttempts >= MaximumExplorerFocusAttempts) return;
                    if (selected.Current.IsOffscreen
                        && selected.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollPattern)
                        && scrollPattern is ScrollItemPattern scrollItem)
                    {
                        scrollItem.ScrollIntoView();
                    }
                    session.FocusAttempts++;
                    selected.SetFocus();
                }

                if (selected.Current.HasKeyboardFocus) ScheduleStabilityCheck(session);
            }
            catch (ElementNotAvailableException)
            {
                RemoveViewHandler(session);
                ObserveExplorerCore(session.Request, session.ExplorerWindow);
                session.Request.SignalFromAutomation();
            }
            catch (COMException)
            {
                RemoveViewHandler(session);
                ObserveExplorerCore(session.Request, session.ExplorerWindow);
                session.Request.SignalFromAutomation();
            }
            catch (InvalidOperationException)
            {
                // Wait for the next selection or structure event if realization changed.
            }
        }

        private static AutomationElement? GetSelectedItem(
            AutomationSession session,
            bool allowTreeLookup)
        {
            if (session.ViewRoot is null) return null;
            if (session.SelectionContainer is null)
            {
                if (!allowTreeLookup) return null;
                session.SelectionContainer = session.ViewRoot.TryGetCurrentPattern(
                    SelectionPattern.Pattern,
                    out _)
                    ? session.ViewRoot
                    : session.ViewRoot.FindFirst(
                        TreeScope.Descendants,
                        session.SelectionContainerCondition!);
            }

            if (session.SelectionContainer is not null
                && session.SelectionContainer.TryGetCurrentPattern(
                    SelectionPattern.Pattern,
                    out var selectionPattern)
                && selectionPattern is SelectionPattern selection)
            {
                var selectedItems = selection.Current.GetSelection();
                return selectedItems.Length == 1 && IsExplorerItem(selectedItems[0])
                    ? selectedItems[0]
                    : null;
            }

            // Some Explorer providers expose SelectionItem without a Selection container.
            if (!allowTreeLookup) return null;
            var matchingItems = session.ViewRoot.FindAll(
                TreeScope.Descendants,
                session.SelectedKeyboardItemCondition!);
            return matchingItems.Count == 1 ? matchingItems[0] : null;
        }

        private static bool IsExplorerItem(AutomationElement item)
        {
            var current = item.Current;
            return current.IsKeyboardFocusable
                && (current.ControlType == ControlType.ListItem
                    || current.ControlType == ControlType.DataItem);
        }

        private void ScheduleStabilityCheck(AutomationSession session)
        {
            session.StabilityTimer!.Change(
                ExplorerFocusStabilityDelay,
                Timeout.InfiniteTimeSpan);
        }

        private void VerifyStableFocus(AutomationSession session)
        {
            if (!ReferenceEquals(activeSession, session) || session.SelectedItem is null) return;
            try
            {
                if (GetForegroundWindow() == session.ExplorerWindow
                    && IsWindowVisible(session.ViewWindow)
                    && session.SelectedItem.Current.IsKeyboardFocusable
                    && session.SelectedItem.Current.HasKeyboardFocus
                    && (bool)session.SelectedItem.GetCurrentPropertyValue(
                        SelectionItemPattern.IsSelectedProperty))
                {
                    CompleteCore(session);
                    return;
                }

                if (!session.StabilityRecoveryUsed)
                {
                    session.StabilityRecoveryUsed = true;
                    TryFocusSelectedItem(session);
                }
            }
            catch (Exception exception) when (exception is COMException
                or ElementNotAvailableException or InvalidOperationException)
            {
                // The hard timeout owns cleanup if Explorer invalidated the element silently.
            }
        }

        private void HandleHardTimeout(ExplorerSelectionFocusRequest request)
        {
            if (!IsActive(request)) return;
            var session = activeSession!;
            if (session.FinalAttemptStarted)
            {
                CompleteCore(session);
                return;
            }

            // Give Explorer one final event-driven correction after a slow first navigation,
            // then tear down deterministically. This is a single fallback, not a retry loop.
            session.FinalAttemptStarted = true;
            session.StabilityRecoveryUsed = false;
            request.PrepareFinalSelection();
            session.TimeoutTimer!.Change(
                ExplorerFocusTimeout,
                Timeout.InfiniteTimeSpan);
        }

        private void FinalPreparationCompletedCore(ExplorerSelectionFocusRequest request)
        {
            if (!IsActive(request) || !activeSession!.FinalAttemptStarted) return;
            var session = activeSession;
            session.TimeoutTimer!.Change(
                ExplorerFocusFinalCleanupDelay,
                Timeout.InfiniteTimeSpan);
            if (session.ViewRoot is not null) TryFocusSelectedItem(session);
        }

        private void CompleteIfActive(ExplorerSelectionFocusRequest request)
        {
            if (IsActive(request)) CompleteCore(activeSession!);
        }

        private bool IsActive(ExplorerSelectionFocusRequest request) =>
            activeSession is { } session
            && ReferenceEquals(session.Request, request)
            && !request.IsStopped;

        private void CompleteCore(AutomationSession session)
        {
            if (!ReferenceEquals(activeSession, session)) return;
            activeSession = null;
            session.TimeoutTimer?.Dispose();
            session.StabilityTimer?.Dispose();
            RemoveViewHandler(session);
            RemoveObservedExplorerHandler(session);

            if (session.WindowHandlerRegistered && session.DesktopRoot is not null)
            {
                try
                {
                    Automation.RemoveAutomationEventHandler(
                        WindowPattern.WindowOpenedEvent,
                        session.DesktopRoot,
                        session.AutomationEventHandler!);
                }
                catch (Exception exception) when (exception is COMException
                    or ElementNotAvailableException or InvalidOperationException or ArgumentException)
                {
                    // A disconnected provider must not prevent the remaining handlers from detaching.
                }
            }
            if (session.SelectionHandlerRegistered && session.DesktopRoot is not null)
            {
                try
                {
                    Automation.RemoveAutomationEventHandler(
                        SelectionItemPattern.ElementSelectedEvent,
                        session.DesktopRoot,
                        session.AutomationEventHandler!);
                }
                catch (Exception exception) when (exception is COMException
                    or ElementNotAvailableException or InvalidOperationException or ArgumentException)
                {
                    // Continue with the process-wide focus handler.
                }
            }
            if (session.FocusHandlerRegistered)
            {
                try
                {
                    Automation.RemoveAutomationFocusChangedEventHandler(session.FocusChangedHandler!);
                }
                catch (Exception exception) when (exception is COMException
                    or InvalidOperationException or ArgumentException)
                {
                    // UIA can deliver a late callback while its provider is disconnecting.
                }
            }
            session.Request.Finish();
        }

        private void RemoveObservedExplorerHandler(AutomationSession session)
        {
            try
            {
                if (session.ObservedStructureHandlerRegistered
                    && session.ObservedRoot is not null
                    && session.ObservedStructureHandler is not null)
                {
                    Automation.RemoveStructureChangedEventHandler(
                        session.ObservedRoot,
                        session.ObservedStructureHandler);
                }
            }
            catch (Exception exception) when (exception is COMException
                or ElementNotAvailableException or InvalidOperationException or ArgumentException)
            {
                // The Explorer root can disappear while a new tab or window is replacing it.
            }
            finally
            {
                Interlocked.Increment(ref session.ObservationVersion);
                session.ObservedStructureHandlerRegistered = false;
                session.ObservedStructureHandler = null;
                session.ObservedRoot = null;
                session.ObservedExplorerWindow = IntPtr.Zero;
            }
        }

        private void RemoveViewHandler(AutomationSession session)
        {
            try
            {
                if (session.StructureHandlerRegistered
                    && session.ViewRoot is not null
                    && session.ViewStructureHandler is not null)
                {
                    Automation.RemoveStructureChangedEventHandler(
                        session.ViewRoot,
                        session.ViewStructureHandler);
                }
            }
            catch (Exception exception) when (exception is COMException
                or ElementNotAvailableException or InvalidOperationException or ArgumentException)
            {
                // The view can disappear during Explorer navigation or tab replacement.
            }
            finally
            {
                Interlocked.Increment(ref session.BindingVersion);
                session.ValidationVersion++;
                session.ValidationPending = false;
                session.StructureHandlerRegistered = false;
                session.ViewStructureHandler = null;
                session.ViewRoot = null;
                session.SelectedItem = null;
                session.SelectionContainer = null;
                session.ViewWindow = IntPtr.Zero;
            }
        }

        private void Invoke(Action operation)
        {
            ready.Task.GetAwaiter().GetResult();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.Add(() =>
            {
                try
                {
                    operation();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            completion.Task.GetAwaiter().GetResult();
        }

        private void Post(Action operation)
        {
            if (!operations.IsAddingCompleted) operations.Add(operation);
        }

        private sealed class AutomationSession(ExplorerSelectionFocusRequest request)
        {
            internal ExplorerSelectionFocusRequest Request { get; } = request;
            internal AutomationElement? DesktopRoot { get; set; }
            internal AutomationElement? ViewRoot { get; set; }
            internal AutomationElement? ObservedRoot { get; set; }
            internal AutomationElement? SelectedItem { get; set; }
            internal AutomationElement? SelectionContainer { get; set; }
            internal Condition? SelectedKeyboardItemCondition { get; set; }
            internal Condition? SelectionContainerCondition { get; set; }
            internal AutomationFocusChangedEventHandler? FocusChangedHandler { get; set; }
            internal AutomationEventHandler? AutomationEventHandler { get; set; }
            internal StructureChangedEventHandler? ObservedStructureHandler { get; set; }
            internal StructureChangedEventHandler? ViewStructureHandler { get; set; }
            internal IntPtr ExplorerWindow { get; set; }
            internal IntPtr ObservedExplorerWindow { get; set; }
            internal IntPtr ViewWindow { get; set; }
            internal bool FocusHandlerRegistered { get; set; }
            internal bool SelectionHandlerRegistered { get; set; }
            internal bool WindowHandlerRegistered { get; set; }
            internal bool ObservedStructureHandlerRegistered { get; set; }
            internal bool StructureHandlerRegistered { get; set; }
            internal long ObservationVersion;
            internal long BindingVersion;
            internal long ValidationVersion { get; set; }
            internal int EventSignalQueued;
            internal int PendingEventSignals;
            internal int FocusAttempts { get; set; }
            internal bool ValidationPending { get; set; }
            internal bool StabilityRecoveryUsed { get; set; }
            internal bool FinalAttemptStarted { get; set; }
            internal System.Threading.Timer? TimeoutTimer { get; set; }
            internal System.Threading.Timer? StabilityTimer { get; set; }
        }

        [Flags]
        private enum AutomationSignal
        {
            Focus = 1,
            Selection = 2,
            Structure = 4,
            Window = 8,
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? File;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Directory;
        internal int Show;
        internal IntPtr Instance;
        internal IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Class;
        internal IntPtr ClassKey;
        internal uint HotKey;
        internal IntPtr Icon;
        internal IntPtr Process;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object? value);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void VTableGap01();
        void VTableGap02();
        void VTableGap03();
        void VTableGap04();
        void VTableGap05();
        void VTableGap06();
        void VTableGap07();
        void VTableGap08();
        void VTableGap09();
        void VTableGap10();
        void VTableGap11();
        void VTableGap12();

        [PreserveSig]
        int QueryActiveShellView(out IShellView? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig]
        int GetWindow(out IntPtr window);
        void VTableGap02();
        void VTableGap03();
        void VTableGap04();
        [PreserveSig]
        int UIActivate(uint state);
        void VTableGap06();
        void VTableGap07();
        void VTableGap08();
        void VTableGap09();
        void VTableGap10();
        void VTableGap11();
        [PreserveSig]
        int SelectItem(IntPtr itemIdList, uint flags);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void VTableGap01();
        void VTableGap02();

        [PreserveSig]
        int GetFolder(ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object? folder);
    }

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2
    {
        void VTableGap01();
        void VTableGap02();

        [PreserveSig]
        int GetCurFolder(out IntPtr itemIdList);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo information);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(
        [MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr ILFindChild(IntPtr parentIdList, IntPtr childIdList);

    [DllImport("shell32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILIsEqual(IntPtr firstItemIdList, IntPtr secondItemIdList);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr itemIdList);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderIdList,
        uint itemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIdLists,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
