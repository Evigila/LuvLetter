using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace LuvLetter.Input;

public sealed class OverlayInputMethodService
{
    private const int ImeCmodeNative = 0x0001;

    public string GetIndicatorText(IntPtr hwnd)
    {
        var cultureCode = MapCultureCode(InputLanguageManager.Current.CurrentInputLanguage);
        if (hwnd == IntPtr.Zero)
        {
            return cultureCode;
        }

        var inputContext = ImmGetContext(hwnd);
        if (inputContext == IntPtr.Zero)
        {
            return cultureCode;
        }

        try
        {
            if (!ImmGetOpenStatus(inputContext))
            {
                return FallbackToLatinIndicator(cultureCode);
            }

            if (
                ImmGetConversionStatus(inputContext, out var conversionMode, out _)
                && (conversionMode & ImeCmodeNative) != 0
            )
            {
                return cultureCode;
            }

            return FallbackToLatinIndicator(cultureCode);
        }
        finally
        {
            _ = ImmReleaseContext(hwnd, inputContext);
        }
    }

    private static string MapCultureCode(CultureInfo? culture)
    {
        var code = culture?.TwoLetterISOLanguageName?.ToUpperInvariant();
        return code switch
        {
            "ZH" => "CN",
            "EN" => "EN",
            "JA" => "JP",
            "KO" => "KR",
            { Length: >= 2 } => code[..2],
            _ => "EN",
        };
    }

    private static string FallbackToLatinIndicator(string cultureCode)
    {
        return cultureCode is "CN" or "JP" or "KR" ? "EN" : cultureCode;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr inputContext);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetOpenStatus(IntPtr inputContext);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetConversionStatus(
        IntPtr inputContext,
        out int conversion,
        out int sentence
    );
}
