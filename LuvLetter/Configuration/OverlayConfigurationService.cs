namespace LuvLetter.Configuration;

public sealed class OverlayConfigurationService : IOverlayConfigurationService
{
    public OverlayLayoutOptions CurrentLayout { get; private set; } = new();

    public event EventHandler<OverlayLayoutOptions>? LayoutChanged;

    public void UpdateLayout(OverlayLayoutOptions layout)
    {
        CurrentLayout = layout;
        LayoutChanged?.Invoke(this, layout);
    }
}
