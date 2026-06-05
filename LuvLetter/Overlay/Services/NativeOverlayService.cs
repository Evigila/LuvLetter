using System.Runtime.InteropServices;
using LuvLetter.Assets;
using LuvLetter.Commands;
using LuvLetter.Configuration;
using LuvLetter.Overlay.Native;

namespace LuvLetter.Overlay.Services;

public sealed class NativeOverlayService : INativeOverlayService
{
    private readonly IAppAssetProvider assetProvider;
    private readonly IOverlayConfigurationService configurationService;
    private readonly CommandDispatcher commandDispatcher;
    private readonly NativeOverlayApi.NativeOverlayEventCallback nativeEventCallback;

    private int started;

    public NativeOverlayService(
        IAppAssetProvider assetProvider,
        IOverlayConfigurationService configurationService,
        CommandDispatcher commandDispatcher)
    {
        this.assetProvider = assetProvider;
        this.configurationService = configurationService;
        this.commandDispatcher = commandDispatcher;
        nativeEventCallback = HandleNativeEvent;

        configurationService.LayoutChanged += (_, layout) => ApplyLayout(layout);
    }

    public bool IsStarted => Interlocked.CompareExchange(ref started, 0, 0) == 1;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) == 1)
        {
            return Task.CompletedTask;
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var logoBytes = assetProvider.LoadOverlayLogoBytes();
                var layout = configurationService.CurrentLayout;

                var logoHandle = GCHandle.Alloc(logoBytes, GCHandleType.Pinned);
                try
                {
                    var options = new NativeOverlayStartOptions
                    {
                        LogoData = logoHandle.AddrOfPinnedObject(),
                        LogoSize = logoBytes.Length,
                        LayoutConfig = ToNativeLayout(layout),
                        InitialInputText = IntPtr.Zero,
                        InitialInputTextLength = 0,
                    };

                    NativeOverlayApi.SetOverlayEventCallback(nativeEventCallback, IntPtr.Zero);
                    var result = NativeOverlayApi.StartOverlay(in options);
                    if (result < 0)
                    {
                        Interlocked.Exchange(ref started, 0);
                        throw new InvalidOperationException(
                            $"Failed to start native overlay. HRESULT: 0x{result:X8}");
                    }
                }
                finally
                {
                    logoHandle.Free();
                }
            },
            cancellationToken);
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref started, 0, 1) == 1)
        {
            NativeOverlayApi.StopOverlay();
        }
    }

    public void SetVisualMode(OverlayVisualMode visualMode)
    {
        if (!IsStarted)
        {
            return;
        }

        var result = NativeOverlayApi.SetOverlayVisualMode((int)visualMode);
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to update overlay visual mode. HRESULT: 0x{result:X8}");
        }
    }

    public void UpdateInputText(string text)
    {
        if (!IsStarted)
        {
            return;
        }

        text ??= string.Empty;
        var result = NativeOverlayApi.UpdateOverlayInputText(text, text.Length);
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to update overlay input text. HRESULT: 0x{result:X8}");
        }
    }

    public void UpdateInputPromptText(string text)
    {
        if (!IsStarted)
        {
            return;
        }

        text ??= string.Empty;
        var result = NativeOverlayApi.UpdateOverlayInputPromptText(text, text.Length);
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Failed to update overlay input prompt text. HRESULT: 0x{result:X8}"
            );
        }
    }

    public void UpdateInputSelection(int selectionStart, int selectionLength, int caretIndex)
    {
        if (!IsStarted)
        {
            return;
        }

        var result = NativeOverlayApi.UpdateOverlayInputSelection(
            selectionStart,
            selectionLength,
            caretIndex
        );
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Failed to update overlay input selection. HRESULT: 0x{result:X8}"
            );
        }
    }

    public void UpdateOutputText(string text)
    {
        if (!IsStarted)
        {
            return;
        }

        text ??= string.Empty;
        var result = NativeOverlayApi.UpdateOverlayOutputText(text, text.Length);
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to update overlay output text. HRESULT: 0x{result:X8}");
        }
    }

    public void UpdateOutputNavigation(bool canPageUp, bool canPageDown)
    {
        if (!IsStarted)
        {
            return;
        }

        var result = NativeOverlayApi.UpdateOverlayOutputNavigation(canPageUp, canPageDown);
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Failed to update overlay output navigation. HRESULT: 0x{result:X8}"
            );
        }
    }

    public void UpdateLogo(byte[] logoBytes)
    {
        if (!IsStarted)
        {
            return;
        }

        var logoHandle = GCHandle.Alloc(logoBytes, GCHandleType.Pinned);
        try
        {
            var result = NativeOverlayApi.UpdateOverlayLogo(logoHandle.AddrOfPinnedObject(), logoBytes.Length);
            if (result < 0)
            {
                throw new InvalidOperationException($"Failed to update overlay logo. HRESULT: 0x{result:X8}");
            }
        }
        finally
        {
            logoHandle.Free();
        }
    }

    private void ApplyLayout(OverlayLayoutOptions layout)
    {
        if (!IsStarted)
        {
            return;
        }

        var nativeLayout = ToNativeLayout(layout);
        var result = NativeOverlayApi.UpdateOverlayLayout(in nativeLayout);
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to update overlay layout. HRESULT: 0x{result:X8}");
        }
    }

    private void HandleNativeEvent(IntPtr eventData, IntPtr context)
    {
        if (eventData == IntPtr.Zero)
        {
            return;
        }

        var nativeEvent = Marshal.PtrToStructure<NativeOverlayEvent>(eventData);
        var text = nativeEvent.Text == IntPtr.Zero || nativeEvent.TextLength <= 0
            ? string.Empty
            : Marshal.PtrToStringUni(nativeEvent.Text, nativeEvent.TextLength) ?? string.Empty;

        if (nativeEvent.Kind != NativeOverlayEventKind.CommandSubmitted)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                var result = await commandDispatcher.DispatchAsync(text).ConfigureAwait(false);
                UpdateOutputText(result.OutputText);
                UpdateInputText(string.Empty);
            });
    }

    private static NativeOverlayLayoutConfig ToNativeLayout(OverlayLayoutOptions layout)
    {
        return new NativeOverlayLayoutConfig
        {
            OverlayWidth = layout.OverlayWidth,
            OverlayHeight = layout.OverlayHeight,
            CommandBarWidth = layout.CommandBarWidth,
            ScreenMarginLeft = layout.ScreenMarginLeft,
            ScreenMarginBottom = layout.ScreenMarginBottom,
            ContentPaddingLeft = layout.ContentPaddingLeft,
            ContentPaddingTop = layout.ContentPaddingTop,
            ContentPaddingRight = layout.ContentPaddingRight,
            ContentPaddingBottom = layout.ContentPaddingBottom,
            LogoWidth = layout.LogoWidth,
            LogoHeight = layout.LogoHeight,
            LogoOffsetX = layout.LogoOffsetX,
            LogoOffsetY = layout.LogoOffsetY,
            CourtesyZoneOffsetX = layout.CourtesyZoneOffsetX,
            CourtesyZoneOffsetY = layout.CourtesyZoneOffsetY,
            CourtesyZoneWidth = layout.CourtesyZoneWidth,
            CourtesyZoneHeight = layout.CourtesyZoneHeight,
            BadgeInactiveDelayMs = layout.BadgeInactiveDelayMs,
            BadgeInactiveOpacity = layout.BadgeInactiveOpacity,
            CommandOutputHeight = layout.CommandOutputHeight,
            TextReservedHeight = layout.TextReservedHeight,
            ElementGap = layout.ElementGap,
            AnimationDurationMs = layout.AnimationDurationMs,
        };
    }
}
