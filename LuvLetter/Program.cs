using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuvLetter;

internal class Program
{
    private static IHost host = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "app.LuvLetter.Mooreforin", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("Program already running.", "Alert");
            return;
        }

        host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddSingleton<App>();
                    services.AddSingleton<MainWindow>();
                }
            )
            .Build();

        var app = Fetch<App>();
        app.InitializeComponent();
        app.Run(Fetch<MainWindow>());
    }

    public static T Fetch<T>()
        where T : class
    {
        return (host.Services.GetRequiredService(typeof(T)) as T)
            ?? throw new Exception("Cannot find service of specified type");
    }
}
