using System.Windows;
using LuvLetter.Assets;

namespace LuvLetter;

public partial class MainWindow : Window
{
    private readonly IAppAssetProvider assetProvider;
    private NotifyIcon notifyIcon = null!;

    public MainWindow(IAppAssetProvider assetProvider)
    {
        this.assetProvider = assetProvider;

        InitializeComponent();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        notifyIcon = new NotifyIcon
        {
            Icon = assetProvider.LoadTrayIcon(),
            Visible = true,
            Text = "LuvLetter",
        };

        notifyIcon.DoubleClick += (s, args) => ShowWindow();

        notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        notifyIcon.ContextMenuStrip.Items.Add("Show", null, (s, e) => ShowWindow());
        notifyIcon.ContextMenuStrip.Items.Add(
            "Exit",
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
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }
}
