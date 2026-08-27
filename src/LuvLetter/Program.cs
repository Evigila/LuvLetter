using LuvLetter.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WpfMessageBox = System.Windows.MessageBox;

namespace LuvLetter;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
            builder.Services.AddLuvLetter(application);
            host = builder.Build();
            var wpfLifetime = host.Services.GetRequiredService<WpfHostLifetime>();
            host.Start();
            wpfLifetime.Run();
        }
        catch (Exception exception)
        {
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
