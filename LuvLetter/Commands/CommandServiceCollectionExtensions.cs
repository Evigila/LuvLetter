using Microsoft.Extensions.DependencyInjection;

namespace LuvLetter.Commands;

public static class CommandServiceCollectionExtensions
{
    public static IServiceCollection AddCommandServices(this IServiceCollection services)
    {
        services.AddSingleton<ICommandParserService, CommandParserService>();
        services.AddSingleton<ICommandRegistry, CommandRegistry>();
        services.AddSingleton<ICommandRouteService, CommandRouteService>();
        services.AddSingleton<INativeCommandExecutor, NativeCommandExecutor>();
        services.AddSingleton<ICommandExecutor, CommandExecutor>();
        services.AddSingleton<CommandDispatcher>();
        return services;
    }
}
