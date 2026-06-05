namespace LuvLetter.Overlay.Services;

public interface INativeOverlayService
{
    bool IsStarted { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
    void SetVisualMode(OverlayVisualMode visualMode);
    void UpdateInputPromptText(string text);
    void UpdateInputText(string text);
    void UpdateInputSelection(int selectionStart, int selectionLength, int caretIndex);
    void UpdateOutputText(string text);
    void UpdateOutputNavigation(bool canPageUp, bool canPageDown);
    void UpdateLogo(byte[] logoBytes);
}
