using LuvLetter.Core.Modules.Indexing;
using LuvLetter.Platform.Indexing;

namespace LuvLetter.Platform.Applications;

internal sealed class ApplicationIndexRefreshRequester(
    FileIndexCompanionClient files,
    WindowsApplicationCatalog applications) : IIndexRefreshRequester
{
    public void RequestRefresh(IndexRefreshMode mode)
    {
        files.RequestRefresh(mode);
        applications.RequestRefresh(mode);
    }
}
