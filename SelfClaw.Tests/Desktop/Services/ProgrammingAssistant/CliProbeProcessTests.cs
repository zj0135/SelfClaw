using System.Diagnostics;
using System.Text;
using FluentAssertions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Tests.Desktop.Services.ProgrammingAssistant;

public sealed class CliProbeProcessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Exit_status_and_both_output_limits_are_preserved_while_pipes_are_drained(int exitCode)
    {
        var output = await new CliProbeProcess().RunAsync(Command(
            $"[Console]::Out.Write(('o' * 800000)); [Console]::Error.Write(('e' * 800000)); exit {exitCode}"),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        output.ExitCode.Should().Be(exitCode);
        output.StandardOutput.Should().HaveLength(CliProbeProcess.MaximumOutputCharacters);
        output.StandardError.Should().HaveLength(CliProbeProcess.MaximumOutputCharacters);
        output.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_timeout_terminate_and_reap_the_owned_process_tree(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "pids.txt");
        var pidTemporaryFile = Path.Combine(directory, "pids.tmp");
        using var cancellation = new CancellationTokenSource();
        var script = "$child = Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru\n" +
            $"[System.IO.File]::WriteAllText('{pidTemporaryFile.Replace("'", "''")}', \"$PID,$($child.Id)\")\n" +
            $"[System.IO.File]::Move('{pidTemporaryFile.Replace("'", "''")}', '{pidFile.Replace("'", "''")}')\nStart-Sleep -Seconds 30";
        var running = new CliProbeProcess().RunAsync(Command(script), TimeSpan.FromSeconds(5), cancellation.Token);
        try
        {
            using var started = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidFile)) { await Task.Delay(10, started.Token); if (running.IsCompleted) await running; }
            var ids = (await File.ReadAllTextAsync(pidFile)).Split(',').Select(int.Parse).ToArray();
            if (cancel) cancellation.Cancel();
            if (cancel) await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
            else await FluentActions.Awaiting(() => running).Should().ThrowAsync<TimeoutException>();
            foreach (var id in ids) IsAlive(id).Should().BeFalse($"owned process {id} must have exited before the probe returns");
        }
        finally
        {
            cancellation.Cancel();
            await running.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default);
            Directory.Delete(directory, true);
        }
    }

    private static CommandInvocation Command(string script) => new()
    {
        FileName = "powershell.exe",
        ArgumentList = ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]
    };

    private static bool IsAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
