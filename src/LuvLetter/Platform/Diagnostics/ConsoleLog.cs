using System.Runtime.CompilerServices;

namespace LuvLetter.Platform.Diagnostics;

internal static class ConsoleLog
{
    internal const string EnvironmentVariableName = "LUVLETTER_CONSOLE_LOG";

    private static int enabled;

    internal static bool IsEnabled => Volatile.Read(ref enabled) != 0;

    internal static void Initialize()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        Volatile.Write(ref enabled, string.Equals(value, "1", StringComparison.Ordinal) ? 1 : 0);
    }

    internal static void WriteLine(string? message)
    {
        if (IsEnabled) Console.WriteLine(message);
    }

    internal static void WriteLine(ref ConsoleLogInterpolatedStringHandler message)
    {
        if (IsEnabled) Console.WriteLine(message.GetFormattedText());
    }

    internal static void WriteError(string? message)
    {
        if (IsEnabled) Console.Error.WriteLine(message);
    }

    internal static void WriteError(object? value)
    {
        if (IsEnabled) Console.Error.WriteLine(value);
    }

    internal static void WriteError(ref ConsoleLogInterpolatedStringHandler message)
    {
        if (IsEnabled) Console.Error.WriteLine(message.GetFormattedText());
    }
}

[InterpolatedStringHandler]
internal ref struct ConsoleLogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler builder;

    public ConsoleLogInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        shouldAppend = ConsoleLog.IsEnabled;
        builder = shouldAppend
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => builder.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => builder.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => builder.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => builder.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) =>
        builder.AppendFormatted(value, alignment, format);

    internal string GetFormattedText() => builder.ToStringAndClear();
}
