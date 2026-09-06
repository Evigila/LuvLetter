using System.Diagnostics;
using System.Collections.Concurrent;
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
        Assert.Equal(
            "luv ",
            CommandInputSyntax.RemoveModePrefixForCompletion(" /luv "));

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
                "tool",
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
        Assert.False(dispatcher.Register("tool", "echo", _ => { }));
        Assert.True(dispatcher.Register("other", "echo", _ => { }));
        Assert.SequenceEqual(["other", "tool"], dispatcher.RegisteredDomainsSnapshot());
        Assert.SequenceEqual(["echo"], dispatcher.RegisteredPathsSnapshot("tool"));
        Assert.True(dispatcher.IsRegisteredDomainInvocation("TOOL anything"));
        Assert.True(dispatcher.IsRegisteredInvocation("tool ECHO value"));
        Assert.False(dispatcher.IsRegisteredInvocation("tool missing"));

        var stopwatch = Stopwatch.StartNew();
        var result = dispatcher.Dispatch("  tool EcHo\t alpha beta  ");
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
            Assert.Equal("tool EcHo\t alpha beta", captured.OriginalText);
            Assert.Equal("tool echo alpha beta", captured.Text);
            Assert.Equal("tool", captured.Domain);
            Assert.Equal("echo", captured.InvokedPath);
            Assert.Equal("echo", captured.CommandPath);
            Assert.Equal("alpha beta", captured.Arguments);

            Assert.Equal(
                CommandDispatchResult.Accepted,
                dispatcher.Dispatch("tool echo queued-one"));
            Assert.Equal(
                CommandDispatchResult.Accepted,
                dispatcher.Dispatch("tool echo queued-two"));
            Assert.Equal(
                CommandDispatchResult.QueueFull,
                dispatcher.Dispatch("tool echo rejected"));
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
        Assert.Equal(
            CommandDispatchResult.RejectedIncomplete,
            dispatcher.Dispatch("tool"));

        dispatcher.Dispose();
        Assert.Equal(
            CommandDispatchResult.Disposed,
            dispatcher.Dispatch("tool echo after-dispose"));

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
                "tool",
                "fail",
                _ => throw new InvalidOperationException("simulated command failure")));
        Assert.Equal(
            CommandDispatchResult.Accepted,
            notificationDispatcher.Dispatch("tool fail now"));
        Assert.True(
            failedRaised.Wait(TimeSpan.FromSeconds(5)),
            "A throwing command did not publish its failure.");
        Assert.Equal("fail", Assert.NotNull(failedInvocation).CommandPath);
        Assert.Equal("simulated command failure", Assert.NotNull(capturedException).Message);

        Assert.Equal(
            CommandDispatchResult.Accepted,
            notificationDispatcher.Dispatch("tool missing argument"));
        Assert.True(
            unhandledRaised.Wait(TimeSpan.FromSeconds(5)),
            "The queue stopped after an event subscriber threw.");
        var capturedUnhandled = Assert.NotNull(unhandledInvocation);
        Assert.Equal("missing argument", capturedUnhandled.CommandPath);
        Assert.Equal("tool", capturedUnhandled.Domain);

        using var routeDispatcher = new CommandDispatcher();
        var routed = new ConcurrentQueue<CommandInvocation>();
        using var routedCompleted = new CountdownEvent(3);
        void Capture(CommandInvocation value)
        {
            routed.Enqueue(value);
            routedCompleted.Signal();
        }

        Assert.True(routeDispatcher.Register(
            "tool",
            "index refresh",
            Capture,
            options:
            [
                new CommandOption(["-f", "--force"], "Force a complete refresh"),
            ]));
        Assert.True(routeDispatcher.Register("tool", "branch child", Capture));
        Assert.True(routeDispatcher.RegisterAlias(
            "tool", "rebuild", "tool", "index refresh"));
        Assert.True(routeDispatcher.RegisterLink(
            "tool", "refreshindex", "tool", "index refresh"));
        Assert.True(routeDispatcher.RegisterLink(
            "tool", "short", "tool", "branch"));
        Assert.False(routeDispatcher.RegisterLink(
            "tool", "dangling", "tool", "absent"));
        Assert.True(routeDispatcher.IsExecutable("tool", "rebuild"));
        Assert.True(routeDispatcher.IsExecutable("tool", "refreshindex"));
        Assert.True(routeDispatcher.HasPath("tool", "branch"));

        Assert.Equal(
            CommandDispatchResult.Accepted,
            routeDispatcher.Dispatch("tool rebuild -f"));
        Assert.Equal(
            CommandDispatchResult.Accepted,
            routeDispatcher.Dispatch("tool refreshindex --force"));
        Assert.Equal(
            CommandDispatchResult.Accepted,
            routeDispatcher.Dispatch("tool short child value"));
        Assert.True(
            routedCompleted.Wait(TimeSpan.FromSeconds(5)),
            "Alias and link routes did not complete.");
        var routedItems = routed.ToArray();
        Assert.SequenceEqual(
            ["index refresh", "index refresh", "branch child"],
            routedItems.Select(static item => item.CommandPath));
        Assert.SequenceEqual(
            ["rebuild", "refreshindex", "short"],
            routedItems.Select(static item => item.InvokedPath));
        Assert.SequenceEqual(
            ["-f", "--force", "value"],
            routedItems.Select(static item => item.Arguments));

        var roots = routeDispatcher.Suggest("tool ", 10);
        Assert.SequenceEqual(
            ["branch", "index", "rebuild", "refreshindex", "short"],
            roots.Select(static suggestion => suggestion.Label));
        var index = routeDispatcher.Suggest("tool i", 10).Single();
        Assert.Equal("index", index.Label);
        Assert.False(index.CanExecute);
        Assert.Equal("/tool index ", index.CompletionText);
        var refresh = routeDispatcher.Suggest("tool index ", 10).Single();
        Assert.Equal("refresh", refresh.Label);
        Assert.True(refresh.CanExecute);
        Assert.Equal(string.Empty, refresh.Description);
        var linkedChild = routeDispatcher.Suggest("tool short ", 10).Single();
        Assert.Equal("child", linkedChild.Label);
        Assert.Equal("tool short child", linkedChild.ExecutionText);
        var options = routeDispatcher.Suggest("tool index refresh -", 10);
        Assert.SequenceEqual(["-f", "--force"], options.Select(static option => option.Label));
        Assert.True(options.All(static option => option.Kind == CommandRouteKind.Option));
        Assert.True(options.All(static option => !option.CanExecute));
        Assert.True(options.All(static option => option.Description == "Force a complete refresh"));
        Assert.SequenceEqual(
            ["/tool index refresh -f ", "/tool index refresh --force "],
            options.Select(static option => option.CompletionText));
        var linkedOptions = routeDispatcher.Suggest("tool refreshindex -", 10);
        Assert.SequenceEqual(
            ["/tool refreshindex -f ", "/tool refreshindex --force "],
            linkedOptions.Select(static option => option.CompletionText));
        Assert.Empty(
            routeDispatcher.Suggest("tool refreshindex -f ", 10),
            "Using one non-repeatable option spelling must suppress all of its aliases.");

        using var cycleDispatcher = new CommandDispatcher();
        Assert.True(cycleDispatcher.Register("tool", "target", _ => { }));
        Assert.True(cycleDispatcher.RegisterLink("tool", "alias", "tool", "target"));
        Assert.True(cycleDispatcher.Unregister("tool", "target"));
        Assert.False(
            cycleDispatcher.RegisterLink("tool", "target", "tool", "alias"),
            "A command-link cycle must be rejected even after its original target is removed.");

        return Task.CompletedTask;
    }
}
