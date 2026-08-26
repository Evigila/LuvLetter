using System.ComponentModel;
using System.Windows;
using LuvLetter.View.Settings;
using LuvLetter.Core.Application;
using Microsoft.Extensions.Hosting;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace LuvLetter.Platform.Tray;

public sealed class TrayIconService : IApplicationShell, IDisposable
{
    private readonly WpfApplication application;
    private readonly IHostApplicationLifetime hostLifetime;
    private readonly Func<SettingsWindow> windowFactory;
    private readonly Forms.ContextMenuStrip contextMenu;
    private readonly Forms.NotifyIcon notifyIcon;
    private SettingsWindow? window;
    private string? cachedStatus;
    public TrayIconService(
        WpfApplication application,
        IHostApplicationLifetime hostLifetime,
        Func<SettingsWindow> windowFactory)
    {
        this.application = application;
        this.hostLifetime = hostLifetime;
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

    public void ReportStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsApplicationStopping())
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            ReportStatusCore(text);
        }
        else
        {
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (!IsApplicationStopping())
                {
                    ReportStatusCore(text);
                }
            });
        }
    }

    public void ShowSettings()
    {
        if (IsApplicationStopping())
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
        if (IsApplicationStopping())
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
        if (!IsApplicationStopping()
            && sender is WpfWindow stateChangedWindow
            && stateChangedWindow.WindowState == WindowState.Minimized)
        {
            MinimizeToTray(stateChangedWindow);
        }
    }

    private void ShowSettingsCore()
    {
        if (IsApplicationStopping())
        {
            return;
        }

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
            notifyIcon.Visible = false;
            hostLifetime.StopApplication();
        });
    }

    private bool IsApplicationStopping() =>
        hostLifetime.ApplicationStopping.IsCancellationRequested
        || application.Dispatcher.HasShutdownStarted
        || application.Dispatcher.HasShutdownFinished;
}
