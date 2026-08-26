using Microsoft.Extensions.Hosting;
using WpfApplication = System.Windows.Application;
using WpfExitEventArgs = System.Windows.ExitEventArgs;

namespace LuvLetter.Hosting;

/// <summary>
/// Bridges WPF dispatcher shutdown with the Generic Host application lifetime.
/// </summary>
internal sealed class WpfHostLifetime(
    WpfApplication application,
    IHostApplicationLifetime hostLifetime) : IHostLifetime, IDisposable
{
    private readonly TaskCompletionSource applicationExited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration stoppingRegistration;
    private int started;
    private int dispatcherRunning;
    private int shutdownRequested;
    private int cleanedUp;

    public void Run()
    {
        Interlocked.Exchange(ref dispatcherRunning, 1);
        try
        {
            if (hostLifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }

            application.Run();
        }
        finally
        {
            Interlocked.Exchange(ref dispatcherRunning, 0);
            applicationExited.TrySetResult();
        }
    }

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        application.Exit += Application_OnExit;
        stoppingRegistration = hostLifetime.ApplicationStopping.Register(
            RequestApplicationShutdown);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestApplicationShutdown();
        if (Volatile.Read(ref dispatcherRunning) == 0
            || application.Dispatcher.HasShutdownFinished)
        {
            applicationExited.TrySetResult();
        }

        await applicationExited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Cleanup();
    }

    public void Dispose() => Cleanup();

    private void Application_OnExit(object sender, WpfExitEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        applicationExited.TrySetResult();
        hostLifetime.StopApplication();
    }

    private void RequestApplicationShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
        {
            return;
        }

        var dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownFinished)
        {
            applicationExited.TrySetResult();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                application.Shutdown();
            }
            else if (!dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.BeginInvoke((Action)application.Shutdown);
            }
        }
        catch (InvalidOperationException) when (
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            if (dispatcher.HasShutdownFinished)
            {
                applicationExited.TrySetResult();
            }
        }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0)
        {
            return;
        }

        application.Exit -= Application_OnExit;
        stoppingRegistration.Dispose();
    }
}
