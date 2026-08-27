using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.Core.NativeShell;
using LuvLetter.Core.Application;
using LuvLetter.Core.Plugins;
using LuvLetter.Platform.Activation;
using LuvLetter.Platform.Tray;
using LuvLetter.View.Settings;
using WpfApplication = System.Windows.Application;

namespace LuvLetter.Hosting;

internal static class ServiceRegistration
{
    internal static IServiceCollection AddLuvLetter(
        this IServiceCollection services,
        App application)
    {
        services.AddSingleton(application);
        services.AddSingleton<WpfApplication>(application);

        services.AddSingleton<LuvLetterConfigurationStore>();
        services.AddSingleton<ILuvLetterConfigurationStore>(
            provider => provider.GetRequiredService<LuvLetterConfigurationStore>());
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<QuickActionRegistry>();

        services.AddSingleton<NativeShellService>();
        services.AddSingleton<INativeShell>(
            provider => provider.GetRequiredService<NativeShellService>());
        services.AddSingleton<INativeConfigurationSink>(
            provider => provider.GetRequiredService<NativeShellService>());

        services.AddSingleton<ActivationGestureService>();
        services.AddSingleton<IActivationGestureService>(
            provider => provider.GetRequiredService<ActivationGestureService>());

        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(
            provider => provider.GetRequiredService<SettingsService>());
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<Func<SettingsWindow>>(
            provider => () => provider.GetRequiredService<SettingsWindow>());

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<IApplicationShell>(
            provider => provider.GetRequiredService<TrayIconService>());
        services.AddSingleton<ILuvLetterPlugin, SettingsPlugin>();

        services.AddSingleton<ApplicationCoordinator>();
        services.AddHostedService(
            provider => provider.GetRequiredService<ApplicationCoordinator>());
        services.AddSingleton<WpfHostLifetime>();
        services.AddSingleton<IHostLifetime>(
            provider => provider.GetRequiredService<WpfHostLifetime>());
        services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        return services;
    }
}
