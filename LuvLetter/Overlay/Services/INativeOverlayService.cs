namespace LuvLetter.Overlay.Services;

public interface INativeOverlayService
{
    bool IsStarted { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
    void SetVisualMode(OverlayVisualMode visualMode);
    void UpdateInputText(string text);
    void UpdateOutputText(string text);
    void UpdateOutputNavigation(bool canPageUp, bool canPageDown);
    void UpdateLogo(byte[] logoBytes);
}
