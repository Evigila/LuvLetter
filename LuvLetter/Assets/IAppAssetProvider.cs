using System.Drawing;

namespace LuvLetter.Assets;

public interface IAppAssetProvider
{
    byte[] LoadOverlayLogoBytes();
    Icon LoadTrayIcon();
}
