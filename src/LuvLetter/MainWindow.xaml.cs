using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuvLetter.Core.Application;
using LuvLetter.Core.Configuration;
using LuvLetter.Settings;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LuvLetter;

public partial class MainWindow : Window
{
    private readonly ConfigurationApplicationService configurationApplicationService;
    private readonly IReadOnlyList<GestureChoice> gestureChoices =
        SettingsChoiceCatalog.Gestures;
    private readonly ObservableCollection<FirstItemKeyChoice> firstItemKeyChoices =
        SettingsChoiceCatalog.CreateFirstItemKeyChoices();
    private readonly IReadOnlyDictionary<WpfTextBox, SettingsHotkeyField> hotkeyFields;

    private LuvLetterConfiguration pendingConfiguration;
    private bool isApplyingConfiguration = true;

    internal MainWindow(ConfigurationApplicationService configurationApplicationService)
    {
        this.configurationApplicationService = configurationApplicationService;
        pendingConfiguration = configurationApplicationService.Current;

        InitializeComponent();
        PositionModeComboBox.ItemsSource = Enum.GetValues<InputBoxPositionMode>();
        InputBoxGestureComboBox.ItemsSource = gestureChoices;
        FeatureWindowGestureComboBox.ItemsSource = gestureChoices;
        FirstItemNumberComboBox.ItemsSource = firstItemKeyChoices;
        hotkeyFields = new Dictionary<WpfTextBox, SettingsHotkeyField>
        {
            [SubmitHotkeyTextBox] = SettingsHotkeyField.InputSubmit,
            [CancelHotkeyTextBox] = SettingsHotkeyField.InputCancel,
            [BackspaceHotkeyTextBox] = SettingsHotkeyField.InputBackspace,
            [FeaturePreviousPageHotkeyTextBox] = SettingsHotkeyField.FeaturePreviousPage,
            [FeatureNextPageHotkeyTextBox] = SettingsHotkeyField.FeatureNextPage,
            [FeatureCancelHotkeyTextBox] = SettingsHotkeyField.FeatureCancel,
        };

        isApplyingConfiguration = false;
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Ready");
    }

    public void SetStatus(string text)
    {
        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Text = text;
        }
    }

    private void ConfigurationTextBox_OnTextChanged(
        object sender,
        TextChangedEventArgs eventArgs
    ) => MarkPending();

    private void ConfigurationComboBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs
    ) => MarkPending();

    private void ConfigurationCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs) =>
        MarkPending();

    private void MarkPending()
    {
        if (!isApplyingConfiguration)
        {
            SetStatus("Pending");
        }
    }

    private void HotkeyTextBox_OnPreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (HotkeyCapture.GetEventKey(eventArgs) == Key.Tab)
        {
            // Keep normal Tab and Shift+Tab focus traversal in read-only hotkey fields.
            return;
        }

        eventArgs.Handled = true;
        if (sender is not WpfTextBox textBox || !hotkeyFields.TryGetValue(textBox, out var field))
        {
            SetStatus("Unknown hotkey field");
            return;
        }

        if (!HotkeyCapture.TryCreate(eventArgs, out var hotkey, out var error))
        {
            SetStatus(error);
            return;
        }

        pendingConfiguration = SettingsHotkeyEditor.Replace(
            pendingConfiguration,
            field,
            hotkey
        );
        RefreshHotkeyControls();
        SetStatus("Pending");
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        // Applying settings is an explicit interaction, not the completion of a
        // Ctrl gesture that happened while editing them. This also invalidates any
        // gesture action already queued on the dispatcher.
        configurationApplicationService.CancelPendingGestures();

        if (!TryReadConfigurationFromControls(out var rawConfiguration, out var error))
        {
            SetStatus(error);
            return;
        }

        var result = configurationApplicationService.Apply(rawConfiguration);
        if (result.DisplayConfiguration is { } displayConfiguration)
        {
            pendingConfiguration = displayConfiguration;
            ApplyConfigurationToControls(displayConfiguration);
        }

        SetStatus(result.Message);
    }

    private void ApplyButton_OnPreviewMouseDown(
        object sender,
        MouseButtonEventArgs eventArgs
    ) => configurationApplicationService.CancelPendingGestures();

    private void ResetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        pendingConfiguration = configurationApplicationService.CreateDefaultConfiguration();
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Pending");
    }
}
