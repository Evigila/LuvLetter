using LuvLetter.Core.Plugins;

namespace LuvLetter.Core.Modules.Indexing;

public interface IIndexRefreshRequester
{
    void RequestRefresh();
}

public sealed class IndexingPlugin(IIndexRefreshRequester refreshRequester) : ILuvLetterPlugin
{
    public string Id => "core.indexing";

    public void Register(PluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterCommand("index.refresh", _ => refreshRequester.RequestRefresh());
    }
}
