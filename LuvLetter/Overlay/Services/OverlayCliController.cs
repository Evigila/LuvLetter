using LuvLetter.Commands;
using LuvLetter.Configuration;

namespace LuvLetter.Overlay.Services;

public enum OverlayCliState
{
    Badge = 0,
    CommandLine = 1,
}

public sealed record OverlayCliInputState(
    string PromptText,
    string Text,
    int SelectionStart,
    int SelectionLength,
    int CaretIndex
);

public sealed class OverlayCliController
{
    private const float ApproxOutputLineHeight = 22.0f;
    private const string DefaultPromptText = "EN";

    private readonly INativeOverlayService nativeOverlayService;
    private readonly CommandDispatcher commandDispatcher;
    private readonly IOverlayConfigurationService configurationService;
    private readonly List<string> outputLines = [];
    private readonly List<string> commandHistory = [];

    private OverlayCliState currentState = OverlayCliState.Badge;
    private string currentPromptText = DefaultPromptText;
    private string currentInputText = string.Empty;
    private int currentSelectionStart;
    private int currentSelectionLength;
    private int currentCaretIndex;
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

    public event EventHandler? StateChanged;

    public OverlayCliState CurrentState => currentState;

    public bool IsOpen => currentState == OverlayCliState.CommandLine;

    public OverlayCliInputState CurrentInputState => CreateInputState(
        currentPromptText,
        currentInputText,
        currentSelectionStart,
        currentSelectionLength,
        currentCaretIndex
    );

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

    public void UpdateInputPromptText(string promptText)
    {
        var nextPromptText = NormalizePromptText(promptText);
        if (string.Equals(currentPromptText, nextPromptText, StringComparison.Ordinal))
        {
            return;
        }

        currentPromptText = nextPromptText;
        SyncInputToOverlay();
    }

    public void UpdateInputState(string text, int selectionStart, int selectionLength, int caretIndex)
    {
        var nextState = CreateInputState(
            currentPromptText,
            text,
            selectionStart,
            selectionLength,
            caretIndex
        );
        if (CurrentInputState == nextState)
        {
            return;
        }

        var textChanged = !string.Equals(currentInputText, nextState.Text, StringComparison.Ordinal);
        SetInputState(nextState);
        if (textChanged)
        {
            BeginManualInputEdit();
        }

        SyncInputToOverlay();
    }

    public OverlayCliInputState RecallPreviousCommand()
    {
        if (!IsOpen || commandHistory.Count == 0)
        {
            return CurrentInputState;
        }

        if (historyIndex == commandHistory.Count)
        {
            historyDraft = currentInputText;
        }

        historyIndex = Math.Max(0, historyIndex - 1);
        return ReplaceInput(commandHistory[historyIndex]);
    }

    public OverlayCliInputState RecallNextCommand()
    {
        if (!IsOpen || commandHistory.Count == 0 || historyIndex >= commandHistory.Count)
        {
            return CurrentInputState;
        }

        historyIndex += 1;
        return ReplaceInput(historyIndex == commandHistory.Count ? historyDraft : commandHistory[historyIndex]);
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

    public async Task<OverlayCliInputState> SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            return CurrentInputState;
        }

        var commandText = currentInputText.Trim();
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return CurrentInputState;
        }

        commandHistory.Add(commandText);
        historyIndex = commandHistory.Count;
        historyDraft = string.Empty;

        var result = await commandDispatcher.DispatchAsync(commandText, cancellationToken);
        var outputLinesBuilder = new System.Text.StringBuilder();
        outputLinesBuilder.Append("> ").Append(commandText);
        if (!string.IsNullOrWhiteSpace(result.OutputText))
        {
            outputLinesBuilder.AppendLine();
            outputLinesBuilder.Append(result.OutputText);
        }

        AppendOutputEntry(outputLinesBuilder.ToString());
        ReplaceInput(string.Empty);
        historyIndex = commandHistory.Count;
        historyDraft = string.Empty;
        SnapOutputViewportToBottom();

        SyncOutputToOverlay();
        return CurrentInputState;
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
        historyIndex = commandHistory.Count;
        if (currentState == OverlayCliState.CommandLine)
        {
            historyDraft = currentInputText;
        }

        ApplyStateToOverlay();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static OverlayVisualMode MapVisualMode(OverlayCliState state)
    {
        return state == OverlayCliState.CommandLine
            ? OverlayVisualMode.CommandLine
            : OverlayVisualMode.Badge;
    }

    private void BeginManualInputEdit()
    {
        historyDraft = currentInputText;
        historyIndex = commandHistory.Count;
    }

    private OverlayCliInputState ReplaceInput(string text)
    {
        var nextState = CreateInputState(
            currentPromptText,
            text,
            text.Length,
            0,
            text.Length
        );
        SetInputState(nextState);
        SyncInputToOverlay();
        return CurrentInputState;
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
        nativeOverlayService.UpdateInputPromptText(currentPromptText);
        nativeOverlayService.UpdateInputText(currentInputText);
        nativeOverlayService.UpdateInputSelection(
            currentSelectionStart,
            currentSelectionLength,
            currentCaretIndex
        );
    }

    private void SyncOutputToOverlay()
    {
        var pageSize = GetVisibleOutputLineCapacity();
        var maxPageStart = GetMaxOutputPageStartLine(pageSize);
        outputPageStartLine = Math.Clamp(outputPageStartLine, 0, maxPageStart);

        var visibleLines = outputLines.Skip(outputPageStartLine).Take(pageSize);
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

    private void SetInputState(OverlayCliInputState inputState)
    {
        currentPromptText = inputState.PromptText;
        currentInputText = inputState.Text;
        currentSelectionStart = inputState.SelectionStart;
        currentSelectionLength = inputState.SelectionLength;
        currentCaretIndex = inputState.CaretIndex;
    }

    private static OverlayCliInputState CreateInputState(
        string promptText,
        string? text,
        int selectionStart,
        int selectionLength,
        int caretIndex
    )
    {
        text ??= string.Empty;
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);

        return new OverlayCliInputState(
            NormalizePromptText(promptText),
            text,
            selectionStart,
            selectionLength,
            caretIndex
        );
    }

    private static string NormalizePromptText(string? promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return DefaultPromptText;
        }

        return promptText.Trim().ToUpperInvariant();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}
