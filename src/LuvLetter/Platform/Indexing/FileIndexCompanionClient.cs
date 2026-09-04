using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using LuvLetter.Core.Application;
using LuvLetter.Core.Modules.Indexing;

namespace LuvLetter.Platform.Indexing;

/// <summary>
/// Supervises the out-of-process indexer. Startup and recovery remain off the host
/// startup path; a missing companion safely produces no file matches.
/// </summary>
internal sealed class FileIndexCompanionClient : IFileIndexClient, IIndexRefreshRequester, IHostedService, IDisposable
{
    private static readonly TimeSpan RebuildingPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StablePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly FileIndexClientOptions options;
    private readonly object sessionStateLock = new();
    private CancellationTokenSource? lifetimeCancellation;
    private Task? supervisorTask;
    private Session? currentSession;
    private FileIndexRuntimeState currentState = FileIndexRuntimeState.Unavailable;
    private long nextRequestId;
    private long currentSessionEpoch;
    private int started;
    private int disposed;
    private int refreshRequested;

    public FileIndexCompanionClient(FileIndexClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    public event Action? IndexChanged;

    public event Action<FileIndexRuntimeState>? StateChanged;

    public FileIndexRuntimeState CurrentState => Volatile.Read(ref currentState);

    public void RequestRefresh()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!options.Maintenance.IsAvailable)
        {
            Console.WriteLine("[Index][configuration-error] Force refresh unavailable | state=configuration-invalid");
            return;
        }
        Interlocked.Exchange(ref refreshRequested, 1);
        Console.WriteLine("[Index][force] Force rebuild queued | state=awaiting-companion");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The file-index companion has already started.");
        }

        if (!options.Maintenance.IsAvailable)
        {
            return Task.CompletedTask;
        }

        lifetimeCancellation = new CancellationTokenSource();
        supervisorTask = SuperviseAsync(lifetimeCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 0) == 0)
        {
            return;
        }

        lifetimeCancellation?.Cancel();
        var supervisor = supervisorTask;
        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCancellation?.IsCancellationRequested == true)
            {
            }
        }

        supervisorTask = null;
        lifetimeCancellation?.Dispose();
        lifetimeCancellation = null;
    }

    public async ValueTask<IReadOnlyList<FileIndexMatch>> QueryAsync(
        string query,
        int maximumResults,
        ulong editorRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (maximumResults <= 0 || query.Length == 0 || cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        var session = Volatile.Read(ref currentSession);
        if (session is null)
        {
            return [];
        }

        var requestId = unchecked((ulong)Interlocked.Increment(ref nextRequestId));
        return await session.QueryAsync(
            requestId,
            query,
            maximumResults,
            editorRevision,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation?.Cancel();
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Session? session = null;
            long sessionEpoch = 0;
            try
            {
                session = await StartSessionAsync(cancellationToken).ConfigureAwait(false);
                lock (sessionStateLock)
                {
                    sessionEpoch = ++currentSessionEpoch;
                    Volatile.Write(ref currentSession, session);
                }
                RaiseIndexChanged();

                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var statusTask = PollStatusAsync(session, sessionEpoch, cancellationToken);
                await Task.WhenAny(
                    session.Process.WaitForExitAsync(),
                    session.Faulted,
                    statusTask,
                    cancellationTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // The command and Global Search candidates remain available while retrying.
            }
            finally
            {
                if (session is not null)
                {
                    lock (sessionStateLock)
                    {
                        if (ReferenceEquals(currentSession, session)
                            && currentSessionEpoch == sessionEpoch)
                        {
                            Volatile.Write(ref currentSession, null);
                            ++currentSessionEpoch;
                            PublishState(FileIndexRuntimeState.Unavailable);
                        }
                    }
                    await session.StopAsync(cancellationToken.IsCancellationRequested)
                        .ConfigureAwait(false);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(options.RestartDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task<Session> StartSessionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var pipeName = $"LuvLetter.Index.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(options.IndexerExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--data-dir");
            startInfo.ArgumentList.Add(options.DataDirectory);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The file-index companion did not start.");
            process.OutputDataReceived += (_, output) =>
            {
                if (output.Data is not null) Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {output.Data}");
            };
            process.ErrorDataReceived += (_, output) =>
            {
                if (output.Data is not null) Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {output.Data}");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectionTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            var helloRequestId = NextRequestId();
            await FileIndexProtocol.WriteFrameAsync(
                pipe,
                FileIndexMessageType.Hello,
                helloRequestId,
                [],
                timeout.Token).ConfigureAwait(false);
            var response = await FileIndexProtocol.ReadFrameAsync(pipe, timeout.Token)
                .ConfigureAwait(false);
            if (response.Type != FileIndexMessageType.HelloAck
                || response.RequestId != helloRequestId)
            {
                throw new InvalidDataException("The indexer did not acknowledge the handshake.");
            }

            await FileIndexProtocol.WriteFrameAsync(
                pipe,
                FileIndexMessageType.ConfigureRoots,
                NextRequestId(),
                FileIndexProtocol.ConfigureRootsPayload(options.NormalizedPartitions(), options.Maintenance),
                timeout.Token).ConfigureAwait(false);

            var session = new Session(pipe, process, options.QueryTimeout);
            pipe = null!;
            process = null;
            return session;
        }
        finally
        {
            pipe?.Dispose();
            if (process is not null)
            {
                TryTerminate(process);
                process.Dispose();
            }
        }
    }

    private ulong NextRequestId() =>
        unchecked((ulong)Interlocked.Increment(ref nextRequestId));

    private async Task PollStatusAsync(
        Session session,
        long sessionEpoch,
        CancellationToken cancellationToken)
    {
        using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.StoppingToken);
        var pollingToken = pollingCancellation.Token;
        FileIndexStatus? previous = null;
        try
        {
            while (!pollingToken.IsCancellationRequested)
            {
                var forceRefresh = Interlocked.Exchange(ref refreshRequested, 0) != 0;
                var status = await session.QueryStatusAsync(
                    NextRequestId(),
                    forceRefresh,
                    pollingToken).ConfigureAwait(false);
                if (status is null)
                {
                    if (forceRefresh) Interlocked.Exchange(ref refreshRequested, 1);
                    return;
                }

                var nextState = new FileIndexRuntimeState(
                    status.Value.Activity switch
                    {
                        FileIndexActivity.Ready => FileIndexRuntimeActivity.Ready,
                        FileIndexActivity.InitialBuild => FileIndexRuntimeActivity.InitialBuild,
                        FileIndexActivity.Updating => FileIndexRuntimeActivity.Updating,
                        FileIndexActivity.Failed => FileIndexRuntimeActivity.Failed,
                        _ => FileIndexRuntimeActivity.Unavailable,
                    },
                    status.Value.IndexGeneration);
                lock (sessionStateLock)
                {
                    if (!ReferenceEquals(currentSession, session)
                        || currentSessionEpoch != sessionEpoch)
                    {
                        return;
                    }

                    PublishState(nextState);
                }

                if ((previous is null && !status.Value.Rebuilding)
                    || (previous is FileIndexStatus prior
                        && (status.Value.IndexGeneration != prior.IndexGeneration
                            || (prior.Rebuilding && !status.Value.Rebuilding))))
                {
                    RaiseIndexChanged();
                }

                previous = status;
                var delay = status.Value.Rebuilding
                    ? RebuildingPollInterval
                    : StablePollInterval;
                await Task.Delay(delay, pollingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pollingToken.IsCancellationRequested)
        {
        }
    }

    private void RaiseIndexChanged()
    {
        foreach (Action handler in IndexChanged?.GetInvocationList().Cast<Action>()
            ?? Array.Empty<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // One observer cannot terminate companion supervision.
            }
        }
    }

    private void PublishState(FileIndexRuntimeState next)
    {
        var previous = Interlocked.Exchange(ref currentState, next);
        if (previous == next)
        {
            return;
        }

        foreach (Action<FileIndexRuntimeState> handler in
            StateChanged?.GetInvocationList().Cast<Action<FileIndexRuntimeState>>()
                ?? Array.Empty<Action<FileIndexRuntimeState>>())
        {
            try
            {
                handler(next);
            }
            catch
            {
                // One observer cannot terminate companion supervision.
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed class Session
    {
        private readonly NamedPipeServerStream pipe;
        private readonly TimeSpan queryTimeout;
        private readonly SemaphoreSlim ioLock = new(1, 1);
        private readonly TaskCompletionSource<bool> faulted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource sessionCancellation = new();
        private int stopped;

        internal Session(NamedPipeServerStream pipe, Process process, TimeSpan queryTimeout)
        {
            this.pipe = pipe;
            this.queryTimeout = queryTimeout;
            Process = process;
        }

        internal Process Process { get; }

        internal Task Faulted => faulted.Task;

        internal CancellationToken StoppingToken => sessionCancellation.Token;

        internal async ValueTask<IReadOnlyList<FileIndexMatch>> QueryAsync(
            ulong requestId,
            string query,
            int maximumResults,
            ulong editorRevision,
            CancellationToken cancellationToken)
        {
            try
            {
                await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return [];
            }

            try
            {
                if (Volatile.Read(ref stopped) != 0 || cancellationToken.IsCancellationRequested)
                {
                    return [];
                }

                // Once a frame is sent, finish reading it even if the editor moves on. This
                // preserves framing; the coordinator discards its revision afterward.
                using var timeout = new CancellationTokenSource(queryTimeout);
                using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    timeout.Token,
                    sessionCancellation.Token);
                await FileIndexProtocol.WriteFrameAsync(
                    pipe,
                    FileIndexMessageType.Query,
                    requestId,
                    FileIndexProtocol.QueryPayload(editorRevision, maximumResults, query),
                    operationCancellation.Token).ConfigureAwait(false);

                while (true)
                {
                    var response = await FileIndexProtocol.ReadFrameAsync(
                        pipe,
                        operationCancellation.Token).ConfigureAwait(false);

                    if (response.RequestId != requestId)
                    {
                        throw new InvalidDataException("The indexer response request ID is invalid.");
                    }

                    if (response.Type == FileIndexMessageType.Error)
                    {
                        return [];
                    }

                    if (response.Type != FileIndexMessageType.QueryResult)
                    {
                        throw new InvalidDataException("The indexer returned an unexpected frame.");
                    }

                    return FileIndexProtocol.ParseQueryResult(
                        response.Payload,
                        editorRevision,
                        maximumResults);
                }
            }
            catch (Exception exception)
            {
                _ = exception;
                faulted.TrySetResult(true);
                return [];
            }
            finally
            {
                ioLock.Release();
            }
        }

        internal async ValueTask<FileIndexStatus?> QueryStatusAsync(
            ulong requestId,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionCancellation.Token);
            try
            {
                await ioLock.WaitAsync(callerCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                if (Volatile.Read(ref stopped) != 0)
                {
                    return null;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation.Token);
                timeout.CancelAfter(queryTimeout);
                await FileIndexProtocol.WriteFrameAsync(
                    pipe,
                    forceRefresh ? FileIndexMessageType.Refresh : FileIndexMessageType.Status,
                    requestId,
                    [],
                    timeout.Token).ConfigureAwait(false);
                var response = await FileIndexProtocol.ReadFrameAsync(pipe, timeout.Token)
                    .ConfigureAwait(false);
                if (response.RequestId != requestId
                    || response.Type != FileIndexMessageType.Status)
                {
                    throw new InvalidDataException("The indexer returned an invalid status response.");
                }

                return FileIndexProtocol.ParseStatus(response.Payload);
            }
            catch (Exception)
            {
                faulted.TrySetResult(true);
                return null;
            }
            finally
            {
                ioLock.Release();
            }
        }

        internal async Task StopAsync(bool graceful)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            sessionCancellation.Cancel();

            if (graceful)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await ioLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        if (pipe.IsConnected)
                        {
                            await FileIndexProtocol.WriteFrameAsync(
                                pipe,
                                FileIndexMessageType.Shutdown,
                                0,
                                [],
                                timeout.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ioLock.Release();
                    }
                }
                catch
                {
                }
            }

            pipe.Dispose();
            try
            {
                if (!Process.HasExited)
                {
                    if (!Process.WaitForExit(500))
                    {
                        TryTerminate(Process);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Process.Dispose();
                sessionCancellation.Dispose();
            }
        }
    }
}
