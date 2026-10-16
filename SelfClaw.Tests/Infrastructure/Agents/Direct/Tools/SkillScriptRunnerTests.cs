using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class SkillScriptRunnerTests : IDisposable
{
    private const string LeakMarker = "SELFCLAW_TEST_LEAK_MARKER";
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".ps1", "powershell.exe")]
    [InlineData(".psm1", "powershell.exe")]
    [InlineData(".cmd", "cmd.exe")]
    [InlineData(".bat", "cmd.exe")]
    [InlineData(".js", "node")]
    [InlineData(".mjs", "node")]
    [InlineData(".cjs", "node")]
    [InlineData(".sh", "bash")]
    public void InterpreterCandidates_map_a_script_extension_to_its_interpreter(string extension, string expected)
        => SkillScriptRunner.InterpreterCandidates(extension).Select(candidate => candidate.Command)
            .Should().Equal(expected);

    [Fact]
    public void InterpreterCandidates_prefers_python_and_falls_back_to_the_py_launcher()
        => SkillScriptRunner.InterpreterCandidates(".py").Select(candidate => candidate.Command)
            .Should().Equal("python", "py");

    [Fact]
    public void InterpreterCandidates_supply_the_flags_each_interpreter_needs()
    {
        SkillScriptRunner.InterpreterCandidates(".ps1").Single().PrefixArguments
            .Should().Equal("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File");
        SkillScriptRunner.InterpreterCandidates(".cmd").Single().PrefixArguments.Should().Equal("/c");
        SkillScriptRunner.InterpreterCandidates(".bat").Single().PrefixArguments.Should().Equal("/c");
        SkillScriptRunner.InterpreterCandidates(".py").Last().PrefixArguments.Should().Equal("-3");
        SkillScriptRunner.InterpreterCandidates(".js").Single().PrefixArguments.Should().BeEmpty();
    }

    [Fact]
    public void InterpreterCandidates_cover_every_executable_script_extension()
    {
        // The only script extensions without an interpreter are the ones the runner deliberately rejects.
        var unsupported = SkillResourceIndexer.ScriptExtensions
            .Where(extension => SkillScriptRunner.InterpreterCandidates(extension).Count == 0)
            .OrderBy(extension => extension, StringComparer.Ordinal)
            .ToArray();

        unsupported.Should().Equal(".cs", ".csx");
    }

    [Fact]
    public async Task RunAsync_executes_the_script_with_the_skill_root_as_working_directory()
    {
        var script = WriteScript("scripts/location.ps1", "[Console]::Out.Write((Get-Location).Path)");

        var result = await new SkillScriptRunner().RunAsync(_rootPath, script, null, 60);

        result.Executed.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be(_rootPath, "the script must run inside the Skill root");
    }

    [Fact]
    public async Task RunAsync_passes_arguments_as_separate_tokens()
    {
        var script = WriteScript(
            "scripts/greet.ps1",
            "param([string]$Name)\n[Console]::Out.Write(\"hello $Name\")");

        var result = await new SkillScriptRunner().RunAsync(_rootPath, script, ["world"], 60);

        result.StandardOutput.Should().Be("hello world");
    }

    [Fact]
    public async Task RunAsync_does_not_inherit_arbitrary_host_environment_variables()
    {
        Environment.SetEnvironmentVariable(LeakMarker, "leaked");
        try
        {
            var script = WriteScript(
                "scripts/environment.ps1",
                $"if ($env:{LeakMarker}) {{ [Console]::Out.Write('leaked') }} else {{ [Console]::Out.Write('clean') }}");

            var result = await new SkillScriptRunner().RunAsync(_rootPath, script, null, 60);

            result.StandardOutput.Should().Be("clean");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LeakMarker, null);
        }
    }

    [Fact]
    public async Task RunAsync_reports_a_non_zero_exit_code_without_throwing()
    {
        var script = WriteScript("scripts/fail.ps1", "exit 3");

        var result = await new SkillScriptRunner().RunAsync(_rootPath, script, null, 60);

        result.Executed.Should().BeTrue();
        result.ExitCode.Should().Be(3);
        result.Message.Should().Contain("exited with code 3");
    }

    [Fact]
    public async Task RunAsync_truncates_high_volume_output()
    {
        var script = WriteScript("scripts/noisy.ps1", "[Console]::Out.Write('x' * 30000)");

        var result = await new SkillScriptRunner().RunAsync(_rootPath, script, null, 60);

        result.OutputTruncated.Should().BeTrue();
        result.StandardOutput.Should().HaveLength(ProcessRunner.MaxOutputCharacters);
    }

    [Fact]
    public async Task RunAsync_times_out_a_script_that_never_finishes()
    {
        var script = WriteScript("scripts/hang.ps1", "while ($true) { Start-Sleep -Milliseconds 50 }");
        var timer = Stopwatch.StartNew();

        Func<Task> run = () => new SkillScriptRunner().RunAsync(_rootPath, script, null, 1);

        await run.Should().ThrowAsync<TimeoutException>().WithMessage("*Skill script timed out after 1 seconds*");
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task RunAsync_rejects_a_missing_interpreter_with_an_actionable_error()
    {
        var script = WriteScript("scripts/deploy.sh", "echo hi");

        Func<Task> run = () => new SkillScriptRunner(_ => null).RunAsync(_rootPath, script, null, 60);

        var exception = await run.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("bash");
        exception.Which.Message.Should().NotContain("WSL", "there is no WSL fallback to promise");
    }

    [Theory]
    [InlineData(".cs")]
    [InlineData(".csx")]
    public async Task RunAsync_refuses_source_files(string extension)
    {
        var script = WriteScript($"scripts/program{extension}", "// code");

        Func<Task> run = () => new SkillScriptRunner().RunAsync(_rootPath, script, null, 60);

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*uses '{extension}'*");
    }

    [Fact]
    public async Task RunAsync_returns_a_failed_result_for_a_missing_script()
    {
        var result = await new SkillScriptRunner().RunAsync(_rootPath, "scripts/absent.ps1", null, 60);

        result.Executed.Should().BeFalse();
        result.Message.Should().Contain("was not found");
    }

    [Fact]
    public async Task RunAsync_rejects_a_path_outside_the_skill_root()
    {
        Func<Task> run = () => new SkillScriptRunner().RunAsync(_rootPath, "../outside.ps1", null, 60);

        await run.Should().ThrowAsync<InvalidDataException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private string WriteScript(string relativePath, string content)
    {
        var path = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return relativePath;
    }
}
