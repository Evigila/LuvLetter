using System.Drawing;
using System.IO;
using System.Windows;

namespace LuvLetter.Assets;

public sealed class AppAssetProvider : IAppAssetProvider
{
    private static readonly Uri FaviconUri = new("pack://application:,,,/LuvLetter;component/favicon.ico");

    public byte[] LoadOverlayLogoBytes()
    {
        using var iconStream =
            System.Windows.Application.GetResourceStream(FaviconUri)?.Stream
            ?? throw new FileNotFoundException("找不到 favicon.ico");

        using var memoryStream = new MemoryStream();
        iconStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public Icon LoadTrayIcon()
    {
        using var iconStream =
            System.Windows.Application.GetResourceStream(FaviconUri)?.Stream
            ?? throw new FileNotFoundException("找不到 favicon.ico");

        return new Icon(iconStream);
    }
}
