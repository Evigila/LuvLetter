using LuvLetter.Hosting;
using LuvLetter.Platform.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMessageBox = System.Windows.MessageBox;

namespace LuvLetter;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConsoleLog.Initialize();
        // Match the companion's UTF-8 logs and the debug launcher's log readers.
        if (ConsoleLog.IsEnabled)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }

        using var mutex = new Mutex(true, "app.LuvLetter.ArkheideSystem", out var isNewInstance);
        if (!isNewInstance)
        {
            WpfMessageBox.Show("LuvLetter is already running.", "LuvLetter");
            return;
        }

        var application = new App();
        IHost? host = null;
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            if (!ConsoleLog.IsEnabled) builder.Logging.ClearProviders();
            builder.Services.AddLuvLetter(application);
            host = builder.Build();
            var wpfLifetime = host.Services.GetRequiredService<WpfHostLifetime>();
            host.Start();
            wpfLifetime.Run();
        }
        catch (Exception exception)
        {
            ConsoleLog.WriteError(exception);
            WpfMessageBox.Show(
                $"Cannot start LuvLetter.\n\n{exception.GetBaseException().Message}",
                "LuvLetter"
            );
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    host.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // The dispatcher is already stopping; container disposal still runs.
                }

                host.Dispose();
            }
        }
    }
}
