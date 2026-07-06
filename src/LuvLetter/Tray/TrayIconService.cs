using System.ComponentModel;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace LuvLetter.Tray;

public sealed class TrayIconService : IDisposable
{
    private readonly WpfApplication application;
    private readonly WpfWindow window;
    private readonly Forms.ContextMenuStrip contextMenu;
    private readonly Forms.NotifyIcon notifyIcon;
    private bool isExiting;

    public TrayIconService(WpfApplication application, WpfWindow window)
    {
        this.application = application;
        this.window = window;

        contextMenu = CreateContextMenu();
        notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = Drawing.SystemIcons.Application,
            Text = "LuvLetter",
            Visible = true,
        };

        notifyIcon.DoubleClick += NotifyIcon_OnDoubleClick;
        window.Closing += Window_OnClosing;
        window.StateChanged += Window_OnStateChanged;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public void StartMinimized()
    {
        window.ShowInTaskbar = false;
        window.WindowState = WindowState.Minimized;
        window.Hide();
    }

    public void Dispose()
    {
        notifyIcon.DoubleClick -= NotifyIcon_OnDoubleClick;
        window.Closing -= Window_OnClosing;
        window.StateChanged -= Window_OnStateChanged;

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
    }

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open LuvLetter", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    private void NotifyIcon_OnDoubleClick(object? sender, EventArgs eventArgs)
    {
        ShowWindow();
    }

    private void Window_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (isExiting)
        {
            return;
        }

        eventArgs.Cancel = true;
        MinimizeToTray();
    }

    private void Window_OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!isExiting && window.WindowState == WindowState.Minimized)
        {
            MinimizeToTray();
        }
    }

    private void ShowWindow()
    {
        application.Dispatcher.Invoke(() =>
        {
            window.ShowInTaskbar = true;
            if (!window.IsVisible)
            {
                window.Show();
            }

            window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }

    private void MinimizeToTray()
    {
        window.ShowInTaskbar = false;
        window.Hide();
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
