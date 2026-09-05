using System.IO;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.View.Settings;
using ArkheideSystem;

namespace LuvLetter.UI.Tests;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var window = new SettingsWindow(new StubSettingsService());
            if (!string.Equals(
                    SurfaceStyleDefaults.FontFamily,
                    window.FontFamily.Source,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected Control Center font family: {window.FontFamily.Source}");
            }

            if (Math.Abs(window.FontSize - SurfaceStyleDefaults.FontSize) > double.Epsilon)
            {
                throw new InvalidOperationException(
                    $"Unexpected Control Center font size: {window.FontSize}");
            }

            window.Close();
            Console.WriteLine("PASS  Control Center XAML construction and typography");
            TestWindowsCommandRunnerAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  Windows command execution and working directory");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL  Control Center XAML construction and typography");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task TestWindowsCommandRunnerAsync()
    {
        using var runner = new WindowsCommandRunner();
        await runner.StartAsync(CancellationToken.None);
        try
        {
            var echo = await ExecuteAsync(runner, "echo command-runner");
            if (echo.ExitCode != 0
                || !echo.StandardOutput.Contains("command-runner", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"cmd.exe output was not captured correctly: {echo.StandardOutput} {echo.StandardError}");
            }

            var quoted = await ExecuteAsync(runner, "echo \"a&b\"");
            if (quoted.ExitCode != 0
                || !quoted.StandardOutput.Contains("\"a&b\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"cmd.exe metacharacter quoting changed: {quoted.StandardOutput} {quoted.StandardError}");
            }

            var failed = await ExecuteAsync(runner, "echo command-failed 1>&2 & exit /b 7");
            if (failed.ExitCode != 7
                || !failed.StandardError.Contains("command-failed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"cmd.exe failure details were not captured correctly: {failed.StandardOutput} {failed.StandardError}");
            }

            var expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var changeDirectory = await ExecuteAsync(runner, $"cd /d \"{expectedDirectory}\"");
            if (changeDirectory.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The command working directory could not be changed: {changeDirectory.StandardError}");
            }

            var currentDirectory = await ExecuteAsync(runner, "cd");
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(currentDirectory.StandardOutput.Trim()),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The command working directory was not preserved: {currentDirectory.StandardOutput}");
            }
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<SystemCommandCompleted> ExecuteAsync(
        WindowsCommandRunner runner,
        string commandText)
    {
        var completion = new TaskCompletionSource<SystemCommandCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(SystemCommandCompleted result) => completion.TrySetResult(result);
        runner.Completed += OnCompleted;
        try
        {
            var enqueue = runner.TryEnqueue(commandText);
            if (enqueue.Status != SystemCommandQueueStatus.Accepted)
            {
                throw new InvalidOperationException(
                    $"Windows command was not accepted: {enqueue.Status}");
            }
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (result.RequestId != enqueue.RequestId)
            {
                throw new InvalidOperationException("Windows command completion used the wrong request id.");
            }
            return result;
        }
        finally
        {
            runner.Completed -= OnCompleted;
        }
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public LuvLetterConfiguration Current => LuvLetterConfiguration.Default;

        public LuvLetterConfiguration CreateDefaultConfiguration() =>
            LuvLetterConfiguration.Default;

        public void CancelPendingGestures()
        {
        }

        public ConfigurationApplicationResult Apply(LuvLetterConfiguration configuration) =>
            throw new NotSupportedException();

        public bool TryMap(
            LuvLetterConfiguration baseline,
            SettingsEditorInput input,
            out LuvLetterConfiguration configuration,
            out string error)
        {
            configuration = baseline;
            error = string.Empty;
            return true;
        }

        public LuvLetterConfiguration ReplaceHotkey(
            LuvLetterConfiguration configuration,
            SettingsHotkeyField field,
            HotkeyDefinition hotkey) => configuration;
    }
}
