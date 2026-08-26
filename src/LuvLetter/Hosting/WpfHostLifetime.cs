using Microsoft.Extensions.Hosting;

namespace LuvLetter.Hosting;

/// <summary>
/// Lets the WPF dispatcher remain the interactive application lifetime while the
/// Generic Host owns service startup and graceful shutdown.
/// </summary>
internal sealed class WpfHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
