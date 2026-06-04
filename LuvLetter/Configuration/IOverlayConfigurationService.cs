namespace LuvLetter.Configuration;

public interface IOverlayConfigurationService
{
    OverlayLayoutOptions CurrentLayout { get; }
    event EventHandler<OverlayLayoutOptions>? LayoutChanged;
    void UpdateLayout(OverlayLayoutOptions layout);
}
