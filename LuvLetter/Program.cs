using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuvLetter;

internal static class Program
{
    private static IHost host = null!;

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "StartOverlay",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    private static extern int StartOverlay(byte[] logoData, int logoSize);

    [DllImport(
        "LuvLetter.Core.dll",
        EntryPoint = "StopOverlay",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall
    )]
    private static extern void StopOverlay();

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
                }
            )
            .Build();

        var app = Fetch<App>();
        var overlayLogoBytes = LoadOverlayLogoBytes();
        var mainWindow = Fetch<MainWindow>();
        var overlayStarted = 0;

        void StartOverlayAfterRender(object? sender, EventArgs e)
        {
            mainWindow.ContentRendered -= StartOverlayAfterRender;

            _ = Task.Run(() =>
            {
                var startResult = StartOverlay(overlayLogoBytes, overlayLogoBytes.Length);
                if (startResult >= 0)
                {
                    Interlocked.Exchange(ref overlayStarted, 1);
                    return;
                }

                app.Dispatcher.BeginInvoke(() =>
                    System.Windows.MessageBox.Show(
                        $"Failed to start native overlay. HRESULT: 0x{startResult:X8}",
                        "Error"
                    )
                );
            });
        }

        mainWindow.ContentRendered += StartOverlayAfterRender;

        app.Exit += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref overlayStarted, 0, 0) == 1)
            {
                StopOverlay();
            }
        };

        app.InitializeComponent();
        app.Run(mainWindow);
    }

    private static byte[] LoadOverlayLogoBytes()
    {
        var resourceUri = new Uri("pack://application:,,,/LuvLetter;component/favicon.ico");
        using var iconStream =
            System.Windows.Application.GetResourceStream(resourceUri)?.Stream
            ?? throw new FileNotFoundException("找不到 favicon.ico");

        using var memoryStream = new MemoryStream();
        iconStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public static T Fetch<T>()
        where T : class
    {
        return (host.Services.GetRequiredService(typeof(T)) as T)
            ?? throw new Exception("Cannot find service of specified type");
    }
}
