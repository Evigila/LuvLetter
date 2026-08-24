using System.Diagnostics;
using LuvLetter.Core.Commands;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestCommandDispatcher()
    {
        using var dispatcher = new CommandDispatcher(capacity: 2);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackCompleted = new ManualResetEventSlim();

        CommandInvocation? invocation = null;
        var callbackThreadId = 0;
        var callerThreadId = Environment.CurrentManagedThreadId;

        Assert.True(
            dispatcher.Register(
                "echo",
                value =>
                {
                    callbackThreadId = Environment.CurrentManagedThreadId;
                    invocation = value;
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    callbackCompleted.Set();
                }));

        var stopwatch = Stopwatch.StartNew();
        var result = dispatcher.Dispatch("  EcHo\t alpha beta  ");
        stopwatch.Stop();

        try
        {
            Assert.Equal(CommandDispatchResult.Accepted, result);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                "Dispatch waited for a blocking handler instead of queuing it.");
            Assert.True(
                callbackStarted.Wait(TimeSpan.FromSeconds(5)),
                "The queued command was not processed.");
            Assert.NotEqual(
                callerThreadId,
                callbackThreadId,
                "The command handler ran inline on the dispatching thread.");

            var captured = Assert.NotNull(invocation);
            Assert.Equal("EcHo\t alpha beta", captured.Text);
            Assert.Equal("EcHo", captured.CommandName);
            Assert.Equal("alpha beta", captured.Arguments);
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.True(
            callbackCompleted.Wait(TimeSpan.FromSeconds(5)),
            "The command handler did not finish after release.");
        Assert.Equal(
            CommandDispatchResult.RejectedEmpty,
            dispatcher.Dispatch(" \t\r\n "));

        dispatcher.Dispose();
        Assert.Equal(
            CommandDispatchResult.Disposed,
            dispatcher.Dispatch("echo after-dispose"));

        return Task.CompletedTask;
    }
}
