using LuvLetter.Assets;
using LuvLetter.Commands;
using LuvLetter.Configuration;
using LuvLetter.Input;
using LuvLetter.Overlay.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuvLetter;

internal static class Program
{
    private static IHost host = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "app.LuvLetter.Mooreforin", out bool isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show("Program already running.", "Alert");
            return;
        }

        host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddSingleton<App>();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<IAppAssetProvider, AppAssetProvider>();
                    services.AddSingleton<
                        IOverlayConfigurationService,
                        OverlayConfigurationService
                    >();
                    services.AddCommandServices();
                    services.AddSingleton<INativeOverlayService, NativeOverlayService>();
                    services.AddSingleton<OverlayCliController>();
                    services.AddSingleton<OverlayInputMethodService>();
                    services.AddSingleton<GlobalKeyboardMonitor>();
                    services.AddSingleton<OverlayCliInputHost>();
                    services.AddSingleton<OverlayApplicationController>();
                }
            )
            .Build();

        var app = Fetch<App>();
        var mainWindow = Fetch<MainWindow>();
        Fetch<OverlayApplicationController>().Attach(app, mainWindow);

        app.InitializeComponent();
        app.Run(mainWindow);
    }

    public static T Fetch<T>()
        where T : class
    {
        return (host.Services.GetRequiredService(typeof(T)) as T)
            ?? throw new Exception("Cannot find service of specified type");
    }
}
