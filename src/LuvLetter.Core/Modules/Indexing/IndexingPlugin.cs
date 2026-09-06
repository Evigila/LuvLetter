using LuvLetter.Core.Plugins;
using ArkheideSystem;

namespace LuvLetter.Core.Modules.Indexing;

public enum IndexRefreshMode
{
    Normal,
    Force,
}

public interface IIndexRefreshRequester
{
    void RequestRefresh(IndexRefreshMode mode);
}

public sealed class IndexingPlugin(IIndexRefreshRequester refreshRequester) : ILuvLetterPlugin
{
    public string Id => "core.indexing";

    public void Register(PluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterCommand(
            "luv",
            "index refresh",
            invocation =>
            {
                var mode = ParseMode(invocation.Arguments);
                refreshRequester.RequestRefresh(mode);
            },
            options:
            [
                new CommandOption(["-f", "--force"], "强制全量刷新"),
            ]);
        context.RegisterCommandLink("luv", "refreshindex", "luv", "index refresh");
    }

    private static IndexRefreshMode ParseMode(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return IndexRefreshMode.Normal;
        }

        return arguments.Trim() switch
        {
            "-f" => IndexRefreshMode.Force,
            "--force" => IndexRefreshMode.Force,
            _ => throw new ArgumentException(
                "Usage: /luv index refresh [-f|--force]"),
        };
    }
}
