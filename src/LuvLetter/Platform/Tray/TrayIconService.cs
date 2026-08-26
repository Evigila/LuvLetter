using System.ComponentModel;
using System.Windows;
using LuvLetter.View.Settings;
using LuvLetter.Core.Runtime;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace LuvLetter.Platform.Tray;

public sealed class TrayIconService : IApplicationShell, IDisposable
{
    private readonly WpfApplication application;
    private readonly Func<SettingsWindow> windowFactory;
    private readonly Forms.ContextMenuStrip contextMenu;
    private readonly Forms.NotifyIcon notifyIcon;
    private SettingsWindow? window;
    private string? cachedStatus;
    private bool isExiting;

    public TrayIconService(WpfApplication application, Func<SettingsWindow> windowFactory)
    {
        this.application = application;
        this.windowFactory = windowFactory;

        contextMenu = CreateContextMenu();
        notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = Drawing.SystemIcons.Application,
            Text = "LuvLetter",
            Visible = true,
        };

        notifyIcon.DoubleClick += NotifyIcon_OnDoubleClick;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public void StartMinimized()
    {
        if (window is not null)
        {
            MinimizeToTray(window);
        }
    }

    public void ReportStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            ReportStatusCore(text);
        }
        else
        {
            _ = application.Dispatcher.BeginInvoke(() => ReportStatusCore(text));
        }
    }

    public void ShowSettings()
    {
        if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            ShowSettingsCore();
        }
        else
        {
            application.Dispatcher.Invoke(ShowSettingsCore);
        }
    }

    public void Dispose()
    {
        notifyIcon.DoubleClick -= NotifyIcon_OnDoubleClick;
        if (window is not null)
        {
            window.Closing -= Window_OnClosing;
            window.StateChanged -= Window_OnStateChanged;
        }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
    }

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open LuvLetter", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    private void NotifyIcon_OnDoubleClick(object? sender, EventArgs eventArgs)
    {
        ShowSettings();
    }

    private void Window_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (isExiting)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (sender is WpfWindow closingWindow)
        {
            MinimizeToTray(closingWindow);
        }
    }

    private void Window_OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!isExiting
            && sender is WpfWindow stateChangedWindow
            && stateChangedWindow.WindowState == WindowState.Minimized)
        {
            MinimizeToTray(stateChangedWindow);
        }
    }

    private void ShowSettingsCore()
    {
        SettingsWindow settingsWindow;
        try
        {
            settingsWindow = GetOrCreateWindow();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"Cannot open LuvLetter settings.\n\n{exception.Message}",
                "LuvLetter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        settingsWindow.ShowInTaskbar = true;
        if (!settingsWindow.IsVisible)
        {
            settingsWindow.Show();
        }

        settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private SettingsWindow GetOrCreateWindow()
    {
        if (window is not null)
        {
            return window;
        }

        var createdWindow = windowFactory();
        createdWindow.Closing += Window_OnClosing;
        createdWindow.StateChanged += Window_OnStateChanged;
        if (cachedStatus is not null)
        {
            createdWindow.SetStatus(cachedStatus);
        }

        window = createdWindow;
        application.MainWindow = createdWindow;
        return createdWindow;
    }

    private void ReportStatusCore(string text)
    {
        cachedStatus = text;
        window?.SetStatus(text);
    }

    private static void MinimizeToTray(WpfWindow targetWindow)
    {
        targetWindow.ShowInTaskbar = false;
        targetWindow.Hide();
    }

    private void Exit()
    {
        application.Dispatcher.Invoke(() =>
        {
            isExiting = true;
            notifyIcon.Visible = false;
            application.Shutdown();
        });
    }
}
