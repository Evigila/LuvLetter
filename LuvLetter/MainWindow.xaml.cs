using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace LuvLetter;

public partial class MainWindow : Window
{
    private NotifyIcon notifyIcon = null!;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        var resourceUri = new Uri("pack://application:,,,/LuvLetter;component/favicon.ico");
        using var iconStream =
            System.Windows.Application.GetResourceStream(resourceUri)?.Stream
            ?? throw new FileNotFoundException("找不到 favicon.ico");

        notifyIcon = new NotifyIcon
        {
            Icon = new Icon(iconStream),
            Visible = true,
            Text = "LuvLetter",
        };

        notifyIcon.DoubleClick += (s, args) => ShowWindow();

        notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        notifyIcon.ContextMenuStrip.Items.Add("显示", null, (s, e) => ShowWindow());
        notifyIcon.ContextMenuStrip.Items.Add(
            "退出",
            null,
            (s, e) =>
            {
                notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
        );
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            this.Hide();
            this.ShowInTaskbar = false;
        }
    }

    private void ShowWindow()
    {
        this.Show();
        this.WindowState = WindowState.Normal;
        this.ShowInTaskbar = true;
        this.Activate();
    }
}
