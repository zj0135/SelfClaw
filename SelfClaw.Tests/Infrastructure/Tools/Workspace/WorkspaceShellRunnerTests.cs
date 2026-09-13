using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Tests.Infrastructure.Tools.Workspace;

public sealed class WorkspaceShellRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    public WorkspaceShellRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Drains_both_high_volume_pipes_while_retaining_only_their_prefixes()
    {
        var result = await new WorkspaceShellRunner().RunShellCommandAsync(_root,
            "$chunk = 'x' * 4096; 1..2048 | ForEach-Object { [Console]::Out.Write($chunk); [Console]::Error.Write($chunk) }; exit 7", 20);

        result.ExitCode.Should().Be(7);
        result.StandardOutput.Should().HaveLength(WorkspaceShellRunner.MaxShellOutputCharacters);
        result.StandardError.Should().HaveLength(WorkspaceShellRunner.MaxShellOutputCharacters);
        result.OutputTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Preserves_unicode_and_newlines()
    {
        var result = await new WorkspaceShellRunner().RunShellCommandAsync(_root,
            "[Console]::Out.Write(\"中文🙂`nsecond\")", 10);
        result.StandardOutput.Should().Be("中文🙂\nsecond");
        result.OutputTruncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_cancellation_finish_pipe_collection(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var timer = Stopwatch.StartNew();
        Func<Task> run = () => new WorkspaceShellRunner().RunShellCommandAsync(_root,
            "while ($true) { [Console]::Out.Write('output'); [Console]::Error.Write('error') }", cancel ? 60 : 1, cancellation.Token);

        if (cancel) await run.Should().ThrowAsync<OperationCanceledException>();
        else await run.Should().ThrowAsync<TimeoutException>();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
