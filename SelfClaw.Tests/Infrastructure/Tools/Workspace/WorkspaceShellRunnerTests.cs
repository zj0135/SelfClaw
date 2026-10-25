using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Processes;
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

    [Theory]
    [InlineData("Get-ChildItem \"$env:LOCALAPPDATA\\SelfClaw\\skills\\review\\scripts\"")]
    [InlineData("Get-ChildItem $env:LOCALAPPDATA/SelfClaw/skills/review")]
    [InlineData("dir %LOCALAPPDATA%\\SelfClaw\\plugins\\office")]
    [InlineData("Get-ChildItem C:\\Users\\me\\AppData\\Local\\selfclaw\\SKILLS")]
    public void TargetsSkillOrPluginInstall_recognizes_an_installation_path(string command)
        => WorkspaceShellRunner.TargetsSkillOrPluginInstall(command).Should().BeTrue();

    [Theory]
    [InlineData("Get-ChildItem .\\skills")]
    [InlineData("dotnet test SelfClaw.slnx")]
    public void TargetsSkillOrPluginInstall_ignores_an_ordinary_command(string command)
        => WorkspaceShellRunner.TargetsSkillOrPluginInstall(command).Should().BeFalse();

    [Theory]
    [InlineData(WorkspaceShellKind.WindowsPowerShell, "powershell.exe")]
    [InlineData(WorkspaceShellKind.PowerShellSeven, "pwsh.exe")]
    [InlineData(WorkspaceShellKind.CommandPrompt, "cmd.exe")]
    public void CreateProcessStartInfo_launches_the_expected_executable_in_the_workspace(
        WorkspaceShellKind kind, string fileName)
    {
        var startInfo = WorkspaceShellRunner.CreateProcessStartInfo(_root, "echo hi", kind);

        startInfo.FileName.Should().Be(fileName);
        startInfo.WorkingDirectory.Should().Be(_root);
    }

    [Theory]
    [InlineData(WorkspaceShellKind.WindowsPowerShell)]
    [InlineData(WorkspaceShellKind.PowerShellSeven)]
    public void CreateProcessStartInfo_base64_encodes_the_command_for_powershell(WorkspaceShellKind kind)
    {
        var startInfo = WorkspaceShellRunner.CreateProcessStartInfo(_root, "echo 中文", kind);

        startInfo.ArgumentList.Take(5).Should().Equal("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand");
        var decoded = System.Text.Encoding.Unicode.GetString(
            Convert.FromBase64String(startInfo.ArgumentList[^1]));
        decoded.Should().Contain("echo 中文");
        decoded.Should().Contain("[Console]::OutputEncoding");
    }

    [Fact]
    public void CreateProcessStartInfo_passes_the_command_verbatim_and_decodes_cmd_as_oem()
    {
        var startInfo = WorkspaceShellRunner.CreateProcessStartInfo(_root, "echo hi", WorkspaceShellKind.CommandPrompt);

        startInfo.ArgumentList.Should().HaveCount(4);
        startInfo.ArgumentList.Take(3).Should().Equal("/d", "/s", "/c");
        startInfo.ArgumentList[^1].Should().Be("echo hi");
        startInfo.StandardOutputEncoding.Should().Be(System.Text.Encoding.GetEncoding(
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
        startInfo.StandardErrorEncoding.Should().Be(startInfo.StandardOutputEncoding);
    }

    [Fact]
    public async Task Command_prompt_runs_batch_syntax_and_reports_the_shell_kind()
    {
        var runner = new WorkspaceShellRunner(new StubShellPreferenceSource(WorkspaceShellKind.CommandPrompt));

        var result = await runner.RunShellCommandAsync(_root, "echo hello & exit 3", 15);

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Trim().Should().Be("hello");
        result.ShellKind.Should().Be(WorkspaceShellKind.CommandPrompt);
        result.Message.Should().Be("cmd exited with code 3.");
    }

    [Fact]
    public async Task Command_prompt_preserves_unicode_output_when_the_oem_codepage_allows_it()
    {
        // cmd built-ins write the pipes in the system OEM code page; on a code page that cannot represent
        // the characters at all this round trip is lossy by design, so only assert where it can hold.
        var oem = System.Text.Encoding.GetEncoding(
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        if (oem.GetString(oem.GetBytes("中文")) != "中文") return;

        var runner = new WorkspaceShellRunner(new StubShellPreferenceSource(WorkspaceShellKind.CommandPrompt));

        var result = await runner.RunShellCommandAsync(_root, "echo 中文", 15);

        result.StandardOutput.Trim().Should().Be("中文");
    }

    [Fact]
    public async Task PowerShell_seven_runs_and_reports_the_shell_kind()
    {
        // A machine without pwsh cannot exercise this; everything assertable without it is covered by
        // the CreateProcessStartInfo tests above.
        if (ShellExecutableLocator.TryFindPowerShellSeven() is null) return;

        var runner = new WorkspaceShellRunner(new StubShellPreferenceSource(WorkspaceShellKind.PowerShellSeven));

        var result = await runner.RunShellCommandAsync(_root, "$PSVersionTable.PSVersion.Major", 20);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().MatchRegex("^[1-9][0-9]*$");
        result.ShellKind.Should().Be(WorkspaceShellKind.PowerShellSeven);
        result.Message.Should().Be("pwsh command completed.");
    }

    [Fact]
    public async Task Git_bash_runs_posix_syntax_and_reports_the_shell_kind()
    {
        if (ShellExecutableLocator.TryFindGitBash() is null) return;

        var runner = new WorkspaceShellRunner(new StubShellPreferenceSource(WorkspaceShellKind.GitBash));

        var result = await runner.RunShellCommandAsync(_root, "echo hello", 15);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be("hello");
        result.ShellKind.Should().Be(WorkspaceShellKind.GitBash);
    }

    [Fact]
    public async Task Git_bash_without_an_installation_fails_with_guidance()
    {
        if (ShellExecutableLocator.TryFindGitBash() is not null) return;

        var runner = new WorkspaceShellRunner(new StubShellPreferenceSource(WorkspaceShellKind.GitBash));

        Func<Task> run = () => runner.RunShellCommandAsync(_root, "echo hi", 10);
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Git for Windows*");
    }

    [Fact]
    public void FindOnPath_skips_system32_for_git_bash()
    {
        var fakeBin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(fakeBin);
        var fakeBash = Path.Combine(fakeBin, "bash.exe");
        File.WriteAllText(fakeBash, string.Empty);
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        ShellExecutableLocator.FindOnPath("bash.exe", $"{system32};{fakeBin}", skipSystem32: true)
            .Should().Be(fakeBash);
    }

    [Fact]
    public void FindOnPath_returns_null_when_no_directory_has_the_file()
        => ShellExecutableLocator.FindOnPath("definitely-missing.exe", _root).Should().BeNull();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class StubShellPreferenceSource(WorkspaceShellKind kind) : IWorkspaceShellPreferenceSource
    {
        public Task<WorkspaceShellPreference> GetPreferenceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceShellPreference(kind));
    }
}
