using System.Text;
using LuvLetter.Commands;
using LuvLetter.Configuration;

namespace LuvLetter.Overlay.Services;

public enum OverlayCliState
{
    Badge = 0,
    CommandLine = 1,
}

public sealed class OverlayCliController
{
    private const float ApproxOutputLineHeight = 22.0f;

    private readonly INativeOverlayService nativeOverlayService;
    private readonly CommandDispatcher commandDispatcher;
    private readonly IOverlayConfigurationService configurationService;
    private readonly StringBuilder inputBuffer = new();
    private readonly List<string> outputLines = [];
    private readonly List<string> commandHistory = [];

    private OverlayCliState currentState = OverlayCliState.Badge;
    private int historyIndex;
    private int outputPageStartLine;
    private string historyDraft = string.Empty;

    public OverlayCliController(
        INativeOverlayService nativeOverlayService,
        CommandDispatcher commandDispatcher,
        IOverlayConfigurationService configurationService
    )
    {
        this.nativeOverlayService = nativeOverlayService;
        this.commandDispatcher = commandDispatcher;
        this.configurationService = configurationService;

        historyIndex = 0;
        configurationService.LayoutChanged += (_, _) => SyncOutputToOverlay();
    }

    public OverlayCliState CurrentState => currentState;

    public bool IsOpen => currentState == OverlayCliState.CommandLine;

    public void Toggle()
    {
        TransitionToState(IsOpen ? OverlayCliState.Badge : OverlayCliState.CommandLine);
    }

    public void Open()
    {
        TransitionToState(OverlayCliState.CommandLine);
    }

    public void Close()
    {
        TransitionToState(OverlayCliState.Badge);
    }

    public void AppendText(string text)
    {
        if (!IsOpen || string.IsNullOrEmpty(text))
        {
            return;
        }

        BeginManualInputEdit();
        inputBuffer.Append(text);
        SyncInputToOverlay();
    }

    public void Backspace()
    {
        if (!IsOpen || inputBuffer.Length == 0)
        {
            return;
        }

        BeginManualInputEdit();
        inputBuffer.Length -= 1;
        SyncInputToOverlay();
    }

    public void RecallPreviousCommand()
    {
        if (!IsOpen || commandHistory.Count == 0)
        {
            return;
        }

        if (historyIndex == commandHistory.Count)
        {
            historyDraft = inputBuffer.ToString();
        }

        historyIndex = Math.Max(0, historyIndex - 1);
        ReplaceInput(commandHistory[historyIndex]);
    }

    public void RecallNextCommand()
    {
        if (!IsOpen || commandHistory.Count == 0 || historyIndex >= commandHistory.Count)
        {
            return;
        }

        historyIndex += 1;
        ReplaceInput(historyIndex == commandHistory.Count ? historyDraft : commandHistory[historyIndex]);
    }

    public void PageOutputUp()
    {
        if (!IsOpen || outputLines.Count == 0)
        {
            return;
        }

        outputPageStartLine = Math.Max(0, outputPageStartLine - GetVisibleOutputLineCapacity());
        SyncOutputToOverlay();
    }

    public void PageOutputDown()
    {
        if (!IsOpen || outputLines.Count == 0)
        {
            return;
        }

        outputPageStartLine = Math.Min(GetMaxOutputPageStartLine(), outputPageStartLine + GetVisibleOutputLineCapacity());
        SyncOutputToOverlay();
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

        commandHistory.Add(commandText);
        historyIndex = commandHistory.Count;
        historyDraft = string.Empty;

        var result = await commandDispatcher.DispatchAsync(commandText, cancellationToken);
        var outputBuilder = new StringBuilder();
        outputBuilder.Append("> ").Append(commandText);
        if (!string.IsNullOrWhiteSpace(result.OutputText))
        {
            outputBuilder.AppendLine();
            outputBuilder.Append(result.OutputText);
        }

        AppendOutputEntry(outputBuilder.ToString());

        inputBuffer.Clear();
        historyIndex = commandHistory.Count;
        historyDraft = string.Empty;
        SnapOutputViewportToBottom();

        SyncInputToOverlay();
        SyncOutputToOverlay();
    }

    public void ApplyStateToOverlay()
    {
        nativeOverlayService.SetVisualMode(MapVisualMode(currentState));
        SyncInputToOverlay();
        SyncOutputToOverlay();
    }

    private void TransitionToState(OverlayCliState nextState)
    {
        if (currentState == nextState)
        {
            ApplyStateToOverlay();
            return;
        }

        currentState = nextState;
        if (currentState == OverlayCliState.CommandLine)
        {
            historyIndex = commandHistory.Count;
            historyDraft = inputBuffer.ToString();
        }

        ApplyStateToOverlay();
    }

    private static OverlayVisualMode MapVisualMode(OverlayCliState state)
    {
        return state == OverlayCliState.CommandLine
            ? OverlayVisualMode.CommandLine
            : OverlayVisualMode.Badge;
    }

    private void BeginManualInputEdit()
    {
        if (historyIndex == commandHistory.Count)
        {
            historyDraft = inputBuffer.ToString();
            return;
        }

        historyDraft = inputBuffer.ToString();
        historyIndex = commandHistory.Count;
    }

    private void ReplaceInput(string text)
    {
        inputBuffer.Clear();
        inputBuffer.Append(text);
        SyncInputToOverlay();
    }

    private void AppendOutputEntry(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (outputLines.Count > 0)
        {
            outputLines.Add(string.Empty);
        }

        foreach (var line in SplitLines(text))
        {
            outputLines.Add(line);
        }
    }

    private void SnapOutputViewportToBottom()
    {
        outputPageStartLine = GetMaxOutputPageStartLine();
    }

    private void SyncInputToOverlay()
    {
        nativeOverlayService.UpdateInputText(inputBuffer.ToString());
    }

    private void SyncOutputToOverlay()
    {
        var pageSize = GetVisibleOutputLineCapacity();
        var maxPageStart = GetMaxOutputPageStartLine(pageSize);
        outputPageStartLine = Math.Clamp(outputPageStartLine, 0, maxPageStart);

        var visibleLines = outputLines
            .Skip(outputPageStartLine)
            .Take(pageSize);
        var visibleText = string.Join(Environment.NewLine, visibleLines);
        var canPageUp = outputPageStartLine > 0;
        var canPageDown = outputPageStartLine + pageSize < outputLines.Count;

        nativeOverlayService.UpdateOutputText(visibleText);
        nativeOverlayService.UpdateOutputNavigation(canPageUp, canPageDown);
    }

    private int GetVisibleOutputLineCapacity()
    {
        var layout = configurationService.CurrentLayout;
        var availableHeight = Math.Max(
            0.0f,
            layout.CommandOutputHeight - layout.ContentPaddingTop - layout.ContentPaddingBottom
        );
        return Math.Max(1, (int)MathF.Floor(availableHeight / ApproxOutputLineHeight));
    }

    private int GetMaxOutputPageStartLine()
    {
        return GetMaxOutputPageStartLine(GetVisibleOutputLineCapacity());
    }

    private int GetMaxOutputPageStartLine(int pageSize)
    {
        return Math.Max(0, outputLines.Count - pageSize);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}
