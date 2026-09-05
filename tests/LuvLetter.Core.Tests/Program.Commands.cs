using System.Diagnostics;
using ArkheideSystem;
using LuvLetter.Core.Commands;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestCommandDispatcher()
    {
        Assert.Equal("settings", CommandInputSyntax.RemoveModePrefix("/settings"));
        Assert.Equal("index.refresh now", CommandInputSyntax.RemoveModePrefix(" /index.refresh now "));
        Assert.Equal("/settings", CommandInputSyntax.RemoveModePrefix("//settings"));
        Assert.Equal(string.Empty, CommandInputSyntax.RemoveModePrefix("/"));

        using var dispatcher = new CommandDispatcher(capacity: 2);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackCompleted = new ManualResetEventSlim();

        CommandInvocation? invocation = null;
        var callbackThreadId = 0;
        var callerThreadId = Environment.CurrentManagedThreadId;
        var completedCount = 0;

        Assert.True(
            dispatcher.Register(
                "echo",
                value =>
                {
                    callbackThreadId = Environment.CurrentManagedThreadId;
                    invocation = value;
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    Interlocked.Increment(ref completedCount);
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

            Assert.Equal(
                CommandDispatchResult.Accepted,
                dispatcher.Dispatch("echo queued-one"));
            Assert.Equal(
                CommandDispatchResult.Accepted,
                dispatcher.Dispatch("echo queued-two"));
            Assert.Equal(
                CommandDispatchResult.QueueFull,
                dispatcher.Dispatch("echo rejected"));
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.True(
            callbackCompleted.Wait(TimeSpan.FromSeconds(5)),
            "The command handler did not finish after release.");
        Assert.True(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref completedCount) == 3,
                TimeSpan.FromSeconds(5)),
            "The dispatcher did not drain accepted commands serially.");
        Assert.Equal(
            CommandDispatchResult.RejectedEmpty,
            dispatcher.Dispatch(" \t\r\n "));

        dispatcher.Dispose();
        Assert.Equal(
            CommandDispatchResult.Disposed,
            dispatcher.Dispatch("echo after-dispose"));

        using var notificationDispatcher = new CommandDispatcher();
        using var failedRaised = new ManualResetEventSlim();
        using var unhandledRaised = new ManualResetEventSlim();
        CommandInvocation? failedInvocation = null;
        CommandInvocation? unhandledInvocation = null;
        Exception? capturedException = null;

        notificationDispatcher.Failed += (_, _) =>
            throw new InvalidOperationException("simulated failed-event subscriber failure");
        notificationDispatcher.Failed += (value, exception) =>
        {
            failedInvocation = value;
            capturedException = exception;
            failedRaised.Set();
        };
        notificationDispatcher.Unhandled += _ =>
            throw new InvalidOperationException("simulated unhandled-event subscriber failure");
        notificationDispatcher.Unhandled += value =>
        {
            unhandledInvocation = value;
            unhandledRaised.Set();
        };

        Assert.True(
            notificationDispatcher.Register(
                "fail",
                _ => throw new InvalidOperationException("simulated command failure")));
        Assert.Equal(
            CommandDispatchResult.Accepted,
            notificationDispatcher.Dispatch("fail now"));
        Assert.True(
            failedRaised.Wait(TimeSpan.FromSeconds(5)),
            "A throwing command did not publish its failure.");
        Assert.Equal("fail", Assert.NotNull(failedInvocation).CommandName);
        Assert.Equal("simulated command failure", Assert.NotNull(capturedException).Message);

        Assert.Equal(
            CommandDispatchResult.Accepted,
            notificationDispatcher.Dispatch("missing argument"));
        Assert.True(
            unhandledRaised.Wait(TimeSpan.FromSeconds(5)),
            "The queue stopped after an event subscriber threw.");
        Assert.Equal("missing", Assert.NotNull(unhandledInvocation).CommandName);

        return Task.CompletedTask;
    }
}
