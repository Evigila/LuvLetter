using System.Text;
using LuvLetter.Commands;

namespace LuvLetter.Overlay.Services;

public sealed class OverlayCliController(
    INativeOverlayService nativeOverlayService,
    CommandDispatcher commandDispatcher
)
{
    private readonly INativeOverlayService nativeOverlayService = nativeOverlayService;
    private readonly CommandDispatcher commandDispatcher = commandDispatcher;
    private readonly StringBuilder inputBuffer = new();

    private int isOpen;
    private string outputText = string.Empty;

    public bool IsOpen => Volatile.Read(ref isOpen) == 1;

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        Open();
    }

    public void Open()
    {
        Interlocked.Exchange(ref isOpen, 1);
        ApplyStateToOverlay();
    }

    public void Close()
    {
        Interlocked.Exchange(ref isOpen, 0);
        nativeOverlayService.SetVisualMode(OverlayVisualMode.Badge);
    }

    public void AppendText(string text)
    {
        if (!IsOpen || string.IsNullOrEmpty(text))
        {
            return;
        }

        inputBuffer.Append(text);
        nativeOverlayService.UpdateInputText(inputBuffer.ToString());
    }

    public void Backspace()
    {
        if (!IsOpen || inputBuffer.Length == 0)
        {
            return;
        }

        inputBuffer.Length -= 1;
        nativeOverlayService.UpdateInputText(inputBuffer.ToString());
    }

    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            return;
        }

        var commandText = inputBuffer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return;
        }

        var result = await commandDispatcher.DispatchAsync(commandText, cancellationToken);
        outputText = result.OutputText;
        inputBuffer.Clear();

        nativeOverlayService.UpdateOutputText(outputText);
        nativeOverlayService.UpdateInputText(string.Empty);
    }

    public void ApplyStateToOverlay()
    {
        nativeOverlayService.SetVisualMode(
            IsOpen ? OverlayVisualMode.CommandLine : OverlayVisualMode.Badge
        );
        nativeOverlayService.UpdateInputText(inputBuffer.ToString());
        nativeOverlayService.UpdateOutputText(outputText);
    }
}
