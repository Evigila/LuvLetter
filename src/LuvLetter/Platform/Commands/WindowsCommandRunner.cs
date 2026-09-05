using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace ArkheideSystem;

internal sealed class WindowsCommandRunner : ISystemCommandRunner, IHostedService, IDisposable
{
    private const int QueueCapacity = 16;
    private const int MaximumCapturedBytesPerStream = 64 * 1024;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(2);
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Channel<CommandRequest> pending = Channel.CreateBounded<CommandRequest>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly object lifecycleLock = new();
    private CancellationTokenSource? lifetimeCancellation;
    private Task? consumer;
    private string currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private long nextRequestId;
    private int started;
    private int stopping;
    private int disposed;

    static WindowsCommandRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public event Action<SystemCommandStarted>? Started;

    public event Action<SystemCommandCompleted>? Completed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The Windows command runner has already started.");
        }

        lock (lifecycleLock)
        {
            lifetimeCancellation = new CancellationTokenSource();
            consumer = ConsumeAsync(lifetimeCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            pending.Writer.TryComplete();
            lock (lifecycleLock)
            {
                lifetimeCancellation?.Cancel();
            }
        }

        Task? completion;
        lock (lifecycleLock)
        {
            completion = consumer;
        }
        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public SystemCommandEnqueueResult TryEnqueue(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        var normalized = commandText.Trim();
        if (normalized.Length == 0)
        {
            return new(SystemCommandQueueStatus.RejectedEmpty, 0);
        }
        if (Volatile.Read(ref started) == 0
            || Volatile.Read(ref stopping) != 0
            || Volatile.Read(ref disposed) != 0)
        {
            return new(SystemCommandQueueStatus.Stopping, 0);
        }

        var requestId = NextRequestId();
        return pending.Writer.TryWrite(new(requestId, normalized))
            ? new(SystemCommandQueueStatus.Accepted, requestId)
            : new(SystemCommandQueueStatus.QueueFull, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref stopping, 1);
        pending.Writer.TryComplete();
        lock (lifecycleLock)
        {
            lifetimeCancellation?.Cancel();
            lifetimeCancellation?.Dispose();
            lifetimeCancellation = null;
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in pending.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                RaiseSafely(Started, new(request.RequestId, request.CommandText));
                var result = await ExecuteAsync(
                    request,
                    currentDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result.WorkingDirectory))
                {
                    currentDirectory = result.WorkingDirectory;
                }
                RaiseSafely(Completed, result.Completion);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<CommandProcessResult> ExecuteAsync(
        CommandRequest request,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (TryExecuteChangeDirectory(
                request,
                workingDirectory,
                stopwatch,
                out var directoryResult))
        {
            return directoryResult;
        }

        var commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandInterpreter)
        {
            Arguments = "/D /U /S /C " + request.CommandText,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not create the command process.");
            process.StandardInput.Close();
            var standardOutput = DrainAsync(process.StandardOutput.BaseStream);
            var standardError = DrainAsync(process.StandardError.BaseStream);
            var timedOut = false;
            var cancelled = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExecutionTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;
                TryKill(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            }

            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            stopwatch.Stop();
            return new(
                new(
                    request.RequestId,
                    request.CommandText,
                    process.HasExited ? process.ExitCode : null,
                    Decode(output.Bytes),
                    Decode(error.Bytes),
                    output.Truncated || error.Truncated,
                    timedOut,
                    cancelled,
                    stopwatch.Elapsed),
                workingDirectory);
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            stopwatch.Stop();
            return new(
                new(
                    request.RequestId,
                    request.CommandText,
                    null,
                    string.Empty,
                    exception.Message,
                    false,
                    false,
                    false,
                    stopwatch.Elapsed),
                workingDirectory);
        }
    }

    private static bool TryExecuteChangeDirectory(
        CommandRequest request,
        string workingDirectory,
        Stopwatch stopwatch,
        out CommandProcessResult result)
    {
        result = default;
        var text = request.CommandText.AsSpan().Trim();
        var separator = IndexOfWhitespace(text);
        var command = separator < 0 ? text : text[..separator];
        if (!command.Equals("cd", StringComparison.OrdinalIgnoreCase)
            && !command.Equals("chdir", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var argument = separator < 0 ? string.Empty : text[(separator + 1)..].Trim().ToString();
        if (argument.StartsWith("/d", StringComparison.OrdinalIgnoreCase)
            && (argument.Length == 2 || char.IsWhiteSpace(argument[2])))
        {
            argument = argument[2..].TrimStart();
        }
        if (argument.Length == 0)
        {
            stopwatch.Stop();
            result = ChangeDirectoryResult(request, workingDirectory, workingDirectory, null, stopwatch.Elapsed);
            return true;
        }
        if (argument.IndexOfAny(['&', '|', '<', '>']) >= 0)
        {
            return false;
        }

        argument = Environment.ExpandEnvironmentVariables(argument);
        if (argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
        {
            argument = argument[1..^1];
        }
        try
        {
            var resolved = Path.GetFullPath(argument, workingDirectory);
            if (!Directory.Exists(resolved))
            {
                stopwatch.Stop();
                result = ChangeDirectoryResult(
                    request,
                    workingDirectory,
                    string.Empty,
                    $"The system cannot find the path specified: {resolved}",
                    stopwatch.Elapsed);
                return true;
            }

            stopwatch.Stop();
            result = ChangeDirectoryResult(request, resolved, resolved, null, stopwatch.Elapsed);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            stopwatch.Stop();
            result = ChangeDirectoryResult(
                request,
                workingDirectory,
                string.Empty,
                exception.Message,
                stopwatch.Elapsed);
            return true;
        }
    }

    private static CommandProcessResult ChangeDirectoryResult(
        CommandRequest request,
        string workingDirectory,
        string output,
        string? error,
        TimeSpan duration) => new(
            new(
                request.RequestId,
                request.CommandText,
                error is null ? 0 : 1,
                output,
                error ?? string.Empty,
                false,
                false,
                false,
                duration),
            workingDirectory);

    private static async Task<CapturedBytes> DrainAsync(Stream stream)
    {
        var retained = new MemoryStream(MaximumCapturedBytesPerStream);
        var buffer = new byte[4096];
        var truncated = false;
        while (true)
        {
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var writable = Math.Min(count, MaximumCapturedBytesPerStream - (int)retained.Length);
            if (writable > 0)
            {
                retained.Write(buffer, 0, writable);
            }
            if (writable < count)
            {
                truncated = true;
            }
        }

        return new(retained.ToArray(), truncated);
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }
        if (LooksLikeUtf16(bytes))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes);
        }
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var nullCount = 0;
        for (var index = 1; index < bytes.Length; index += 2)
        {
            if (bytes[index] == 0)
            {
                nullCount++;
            }
        }
        return nullCount >= Math.Max(1, bytes.Length / 16);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int IndexOfWhitespace(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private long NextRequestId()
    {
        var requestId = Interlocked.Increment(ref nextRequestId);
        return requestId == 0 ? Interlocked.Increment(ref nextRequestId) : requestId;
    }

    private static void RaiseSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // Presentation consumers cannot terminate command execution.
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private readonly record struct CommandRequest(long RequestId, string CommandText);

    private readonly record struct CapturedBytes(byte[] Bytes, bool Truncated);

    private readonly record struct CommandProcessResult(
        SystemCommandCompleted Completion,
        string WorkingDirectory);
}
