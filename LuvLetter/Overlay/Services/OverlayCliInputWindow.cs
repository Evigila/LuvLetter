using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MediaBrushes = System.Windows.Media.Brushes;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LuvLetter.Overlay.Services;

internal sealed class OverlayCliInputWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    public OverlayCliInputWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Focusable = true;
        SnapsToDevicePixels = true;

        var root = new Grid
        {
            Background = MediaBrushes.Transparent,
            Focusable = false,
        };

        InputTextBox = new WpfTextBox
        {
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0.0),
            Foreground = MediaBrushes.Transparent,
            CaretBrush = MediaBrushes.Transparent,
            SelectionBrush = MediaBrushes.Transparent,
            SelectionOpacity = 0.0,
            FontSize = 18.0,
            Padding = new Thickness(0.0),
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = false,
            AcceptsTab = false,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Opacity = 0.01,
        };

        root.Children.Add(InputTextBox);
        Content = root;

        PreviewMouseDown += (_, _) => FocusInput();
    }

    public WpfTextBox InputTextBox { get; }

    public IntPtr Handle
    {
        get
        {
            var helper = new WindowInteropHelper(this);
            return helper.EnsureHandle();
        }
    }

    public void ApplyLayout(Rect windowBounds, Rect inputBounds)
    {
        Left = windowBounds.Left;
        Top = windowBounds.Top;
        Width = windowBounds.Width;
        Height = windowBounds.Height;

        InputTextBox.Margin = new Thickness(
            inputBounds.Left,
            inputBounds.Top,
            Math.Max(0.0, windowBounds.Width - inputBounds.Right),
            Math.Max(0.0, windowBounds.Height - inputBounds.Bottom)
        );
        InputTextBox.Width = Math.Max(0.0, inputBounds.Width);
        InputTextBox.Height = Math.Max(0.0, inputBounds.Height);
    }

    public void ShowAndFocus()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        FocusInput();
    }

    public void FocusInput()
    {
        _ = InputTextBox.Focus();
        Keyboard.Focus(InputTextBox);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            var extendedStyle = GetWindowLongPtr(source.Handle, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow;
            _ = SetWindowLongPtr(source.Handle, GwlExStyle, new IntPtr(extendedStyle));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);
}
