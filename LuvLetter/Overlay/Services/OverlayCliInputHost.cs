using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuvLetter.Configuration;
using LuvLetter.Input;
using FormsScreen = System.Windows.Forms.Screen;

namespace LuvLetter.Overlay.Services;

public sealed class OverlayCliInputHost : IDisposable
{
    private const double PromptWidth = 28.0;
    private const double PromptGap = 6.0;

    private readonly OverlayCliController cliController;
    private readonly IOverlayConfigurationService configurationService;
    private readonly OverlayInputMethodService inputMethodService;

    private OverlayCliInputWindow? inputWindow;
    private bool started;
    private bool isApplyingInputState;
    private IntPtr previousForegroundWindow;

    public OverlayCliInputHost(
        OverlayCliController cliController,
        IOverlayConfigurationService configurationService,
        OverlayInputMethodService inputMethodService
    )
    {
        this.cliController = cliController;
        this.configurationService = configurationService;
        this.inputMethodService = inputMethodService;
    }

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        cliController.StateChanged += HandleCliStateChanged;
        configurationService.LayoutChanged += HandleLayoutChanged;
        InputLanguageManager.Current.InputLanguageChanged += HandleInputLanguageChanged;
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

        started = false;
        cliController.StateChanged -= HandleCliStateChanged;
        configurationService.LayoutChanged -= HandleLayoutChanged;
        InputLanguageManager.Current.InputLanguageChanged -= HandleInputLanguageChanged;

        if (inputWindow is not null)
        {
            inputWindow.Close();
            inputWindow = null;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void HandleCliStateChanged(object? sender, EventArgs eventArgs)
    {
        InvokeOnUiThread(UpdateVisibility);
    }

    private void HandleLayoutChanged(object? sender, OverlayLayoutOptions layout)
    {
        InvokeOnUiThread(ApplyLayout);
    }

    private void HandleInputLanguageChanged(object? sender, InputLanguageEventArgs eventArgs)
    {
        InvokeOnUiThread(RefreshInputPromptText);
    }

    private void UpdateVisibility()
    {
        if (!started)
        {
            return;
        }

        var window = EnsureWindow();
        ApplyLayout();

        if (cliController.IsOpen)
        {
            if (previousForegroundWindow == IntPtr.Zero)
            {
                previousForegroundWindow = GetForegroundWindow();
            }

            ApplyInputState(cliController.CurrentInputState);
            window.ShowAndFocus();
            RefreshInputPromptText();
            return;
        }

        if (window.IsVisible)
        {
            window.Hide();
        }

        previousForegroundWindow = RestoreForegroundWindow(previousForegroundWindow);
    }

    private void ApplyLayout()
    {
        if (inputWindow is null)
        {
            return;
        }

        var layout = ComputeLayoutProjection(configurationService.CurrentLayout);
        inputWindow.ApplyLayout(layout.WindowBounds, layout.InputBounds);
    }

    private void RefreshInputPromptText()
    {
        if (!started || !cliController.IsOpen)
        {
            return;
        }

        var window = EnsureWindow();
        cliController.UpdateInputPromptText(inputMethodService.GetIndicatorText(window.Handle));
    }

    private OverlayCliInputWindow EnsureWindow()
    {
        if (inputWindow is not null)
        {
            return inputWindow;
        }

        inputWindow = new OverlayCliInputWindow();
        var inputTextBox = inputWindow.InputTextBox;
        System.Windows.Controls.SpellCheck.SetIsEnabled(inputTextBox, false);
        inputTextBox.TextChanged += HandleTextChanged;
        inputTextBox.SelectionChanged += HandleSelectionChanged;
        inputTextBox.GotKeyboardFocus += (_, _) => RefreshInputPromptText();
        inputTextBox.PreviewKeyDown += HandlePreviewKeyDown;
        inputTextBox.PreviewKeyUp += (_, _) => RefreshInputPromptText();

        ApplyLayout();
        return inputWindow;
    }

    private void HandleTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        SyncTextBoxStateToController();
    }

    private void HandleSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        SyncTextBoxStateToController();
    }

    private async void HandlePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (!cliController.IsOpen)
        {
            return;
        }

        if (eventArgs.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            cliController.Close();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            var nextState = await cliController.SubmitAsync();
            ApplyInputState(nextState);
            return;
        }

        if (eventArgs.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            ApplyInputState(cliController.RecallPreviousCommand());
            return;
        }

        if (eventArgs.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            ApplyInputState(cliController.RecallNextCommand());
            return;
        }

        if (eventArgs.Key == Key.PageUp && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            cliController.PageOutputUp();
            return;
        }

        if (eventArgs.Key == Key.PageDown && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            cliController.PageOutputDown();
        }
    }

    private void ApplyInputState(OverlayCliInputState inputState)
    {
        var window = EnsureWindow();
        var inputTextBox = window.InputTextBox;

        isApplyingInputState = true;
        try
        {
            if (!string.Equals(inputTextBox.Text, inputState.Text, StringComparison.Ordinal))
            {
                inputTextBox.Text = inputState.Text;
            }

            inputTextBox.Select(inputState.SelectionStart, inputState.SelectionLength);
            if (inputState.SelectionLength == 0)
            {
                inputTextBox.CaretIndex = inputState.CaretIndex;
            }
        }
        finally
        {
            isApplyingInputState = false;
        }
    }

    private void SyncTextBoxStateToController()
    {
        if (isApplyingInputState || inputWindow is null || !cliController.IsOpen)
        {
            return;
        }

        var inputTextBox = inputWindow.InputTextBox;
        cliController.UpdateInputState(
            inputTextBox.Text,
            inputTextBox.SelectionStart,
            inputTextBox.SelectionLength,
            inputTextBox.CaretIndex
        );
    }

    private static OverlayCliInputWindowLayout ComputeLayoutProjection(OverlayLayoutOptions layout)
    {
        var workingArea = FormsScreen.PrimaryScreen?.WorkingArea ?? FormsScreen.PrimaryScreen?.Bounds ?? default;
        var badgeWidth = Math.Max(1, layout.OverlayWidth);
        var badgeHeight = Math.Max(1, layout.OverlayHeight);
        var commandWidth = Math.Max(badgeWidth, layout.CommandBarWidth);
        var outputHeight = Math.Max(0.0, layout.CommandOutputHeight);
        var commandGap = outputHeight > 0.0 ? Math.Max(0.0, layout.ElementGap) : 0.0;

        var visibleLeft = workingArea.Left + layout.ScreenMarginLeft;
        var badgeTop = workingArea.Bottom - layout.ScreenMarginBottom - badgeHeight;
        var windowTop = badgeTop - outputHeight - commandGap;
        var windowHeight = badgeHeight + outputHeight + commandGap;
        var inputBarTop = windowHeight - badgeHeight;

        var promptLeft = Math.Min(commandWidth, badgeWidth + commandGap);
        var promptRight = Math.Min(commandWidth, promptLeft + PromptWidth);
        var inputLeft = Math.Min(commandWidth, promptRight + PromptGap);
        var inputRight = Math.Max(inputLeft, commandWidth - layout.ContentPaddingRight);

        return new OverlayCliInputWindowLayout(
            new Rect(visibleLeft, windowTop, commandWidth, Math.Max(1.0, windowHeight)),
            new Rect(
                inputLeft,
                inputBarTop,
                Math.Max(0.0, inputRight - inputLeft),
                badgeHeight
            )
        );
    }

    private static IntPtr RestoreForegroundWindow(IntPtr previousWindow)
    {
        if (previousWindow == IntPtr.Zero || !IsWindow(previousWindow))
        {
            return IntPtr.Zero;
        }

        _ = SetForegroundWindow(previousWindow);
        return IntPtr.Zero;
    }

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    private sealed record OverlayCliInputWindowLayout(Rect WindowBounds, Rect InputBounds);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
