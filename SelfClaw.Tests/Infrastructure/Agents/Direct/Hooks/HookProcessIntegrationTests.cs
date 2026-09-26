using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// Exercises the real process path with powershell.exe fixtures. These tests spawn processes, so they
/// are opt-out via the HookProcess category.
/// </summary>
[Trait("Category", "HookProcess")]
public sealed class HookProcessIntegrationTests
{
    private static readonly string FixturesPath =
        Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Agents", "Direct", "Hooks", "Fixtures");

    [Fact]
    public async Task A_successful_hook_returns_its_stdout_and_exit_code()
    {
        var result = await RunAsync("block.ps1");

        result.Exit.Should().Be(HookProcessExit.Exited);
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("blocked by fixture");
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_reported()
    {
        var result = await RunAsync("exitcode.ps1");

        result.Exit.Should().Be(HookProcessExit.Exited);
        result.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task A_hook_that_exceeds_the_stdout_cap_is_terminated()
    {
        var result = await RunAsync("bigoutput.ps1");

        result.Exit.Should().Be(HookProcessExit.OutputTooLarge);
    }

    [Fact]
    public async Task A_hook_that_times_out_is_terminated()
    {
        var result = await RunAsync("timeout.ps1", timeout: TimeSpan.FromSeconds(2));

        result.Exit.Should().Be(HookProcessExit.TimedOut);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_throws_OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var action = () => RunAsync("timeout.ps1", TimeSpan.FromSeconds(30), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_missing_executable_is_a_launch_failure()
    {
        var runner = new CommandHookRunner();
        var start = new HookProcessStart(
            "definitely-not-a-real-executable.exe",
            [],
            Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await runner.RunAsync(start, ReadOnlyMemory<byte>.Empty, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Exit.Should().Be(HookProcessExit.LaunchFailed);
    }

    [Fact]
    public async Task An_orphaned_child_process_is_terminated_with_the_job()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync("orphan.ps1", TimeSpan.FromSeconds(20));
        stopwatch.Stop();
        var childPid = ParseChildPid(result.StderrTail);

        result.Exit.Should().Be(HookProcessExit.Exited);
        result.ExitCode.Should().Be(0);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !IsRunning(childPid));
    }

    [Fact]
    public async Task The_hook_environment_clears_inherited_selfclaw_variables_and_keeps_the_rest()
    {
        Environment.SetEnvironmentVariable("SELFCLAW_TEST", "inherited");
        Environment.SetEnvironmentVariable("MY_TEST_ENV", "keep");
        try
        {
            var workingDirectory = Path.GetTempPath();
            var runner = new CommandHookRunner();
            var start = new HookProcessStart(
                "powershell.exe",
                PowerArgs("environment.ps1"),
                workingDirectory,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["SELFCLAW_HOOK_EVENT"] = "toolExecuting" });

            var result = await runner.RunAsync(start, ReadOnlyMemory<byte>.Empty, TimeSpan.FromSeconds(20), CancellationToken.None);

            result.Stdout.Should().Contain("event=toolExecuting");
            result.Stdout.Should().Contain("test=unset");
            result.Stdout.Should().Contain("other=keep");
            result.Stdout.Should().Contain(workingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELFCLAW_TEST", null);
            Environment.SetEnvironmentVariable("MY_TEST_ENV", null);
        }
    }

    [Fact]
    public async Task A_real_runStarting_hook_can_block_the_turn()
    {
        var outcome = await ExecuteAsync(
            Hook("block.ps1", PluginHookEvent.RunStarting),
            hooks => hooks.RunStartingAsync(
                new RunStartingInput("Provider", AiProviderKind.OpenAI, "model", "prompt", [], ["read_file"], null),
                CancellationToken.None));

        outcome.BlockedBy.Should().Be(new HookSource("alpha", "a"));
        outcome.BlockReason.Should().Be("Blocked by hook 'alpha/a': blocked by fixture");
    }

    [Fact]
    public async Task A_real_toolExecuting_hook_can_deny()
    {
        var deny = await ToolHookAsync("deny.ps1", PluginHookEvent.ToolExecuting);

        deny.BlockedBy.Should().Be(new HookSource("alpha", "a"));
        deny.BlockReason.Should().Be("Blocked by hook 'alpha/a': denied by fixture");
    }

    [Fact]
    public async Task A_real_toolExecuting_hook_can_require_approval()
    {
        var outcome = await ToolHookAsync("ask.ps1", PluginHookEvent.ToolExecuting);

        outcome.ApprovalRequiredBy.Should().ContainSingle().Which.Should().Be(new HookSource("alpha", "a"));
        outcome.ApprovalReason.Should().Be("ask by fixture");
    }

    [Fact]
    public async Task A_real_toolExecuting_hook_can_rewrite_arguments()
    {
        var function = AIFunctionFactory.Create((string command) => command, "run_shell_command", "Runs a command.");
        var binding = new DirectToolBinding(
            function,
            new DirectToolDescriptor("run_shell_command", ToolCallKind.Run));
        var call = new ToolHookCall(
            "call-1",
            0,
            binding,
            new AIFunctionArguments { ["command"] = "echo dangerous" },
            ToolPermissionMode.RequireApproval);

        var outcome = await ExecuteAsync(
            Hook("rewrite.ps1", PluginHookEvent.ToolExecuting),
            hooks => hooks.ToolExecutingAsync(call, CancellationToken.None));

        outcome.EffectiveArguments!["command"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("echo safe");
    }

    [Fact]
    public async Task A_real_toolExecuted_hook_returns_feedback()
    {
        var outcome = await ExecuteAsync(
            Hook("feedback.ps1", PluginHookEvent.ToolExecuted),
            hooks => hooks.ToolExecutedAsync(Call(), Result(), CancellationToken.None));

        outcome.Feedback.Should().ContainSingle().Which.Text.Should().Be("fixture feedback");
    }

    [Fact]
    public async Task A_real_httpRequestSending_hook_adds_a_header()
    {
        var outcome = await ExecuteAsync(
            Hook("addheaders.ps1", PluginHookEvent.HttpRequestSending),
            hooks => hooks.HttpRequestSendingAsync(
                new HttpHookRequest(1, new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1"), null, false),
                CancellationToken.None));

        outcome.Headers.Should().Equal([new KeyValuePair<string, string>("x-fixture", "yes")]);
    }

    [Fact]
    public async Task A_real_invalid_json_stdout_fails_the_hook()
    {
        var outcome = await ExecuteAsync(
            Hook("invalidjson.ps1", PluginHookEvent.ToolExecuting),
            hooks => hooks.ToolExecutingAsync(Call(), CancellationToken.None));

        outcome.IgnoredFailures.Should().ContainSingle().Which.Kind.Should().Be("invalidOutput");
    }

    private static async Task<ToolExecutingOutcome> ToolHookAsync(string fixture, PluginHookEvent hookEvent)
        => await ExecuteAsync(
            Hook(fixture, hookEvent),
            hooks => hooks.ToolExecutingAsync(Call(), CancellationToken.None));

    private static async Task<T> ExecuteAsync<T>(
        ResolvedPluginHook hook,
        Func<DirectTurnHooks, Task<T>> action)
    {
        var turnHooks = HookTestFactory.Create(
            HookTestFactory.CreateContext(),
            [hook],
            new CommandHookRunner(NullLogger<CommandHookRunner>.Instance).RunAsync);
        return await action(turnHooks);
    }

    private static ResolvedPluginHook Hook(string fixture, PluginHookEvent hookEvent)
        => new(
            "alpha",
            "1.0.0",
            Path.GetTempPath(),
            new PluginHookContribution(
                "a",
                hookEvent,
                HookTestFactory.EmptyMatcher,
                "powershell.exe",
                PowerArgs(fixture),
                TimeSpan.FromSeconds(10),
                PluginHookFailurePolicy.Continue,
                RunAsync: false,
                IncludeRequestBody: false),
            DeclarationOrder: 0,
            Inherited: false);

    private static ToolHookCall Call()
    {
        var function = AIFunctionFactory.Create((string command) => command, "run_shell_command", "Runs a command.");
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("run_shell_command", ToolCallKind.Run));
        return new ToolHookCall(
            "call-1",
            0,
            binding,
            new AIFunctionArguments { ["command"] = "echo dangerous" },
            ToolPermissionMode.RequireApproval);
    }

    private static ToolHookResult Result()
        => new(
            ToolCallStatus.Completed,
            DeniedBy: null,
            Summary: "ran",
            Content: JsonSerializer.SerializeToElement("done"),
            Error: null,
            EffectiveArgumentsJson: null,
            Duration: TimeSpan.FromMilliseconds(1));

    private static async Task<HookProcessResult> RunAsync(
        string fixture,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var runner = new CommandHookRunner(NullLogger<CommandHookRunner>.Instance);
        var start = new HookProcessStart(
            "powershell.exe",
            PowerArgs(fixture),
            Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["SELFCLAW_HOOK_EVENT"] = "toolExecuting" });
        return await runner.RunAsync(
            start,
            "{}"u8.ToArray(),
            timeout ?? TimeSpan.FromSeconds(20),
            cancellationToken);
    }

    private static IReadOnlyList<string> PowerArgs(string fixture)
        =>
        [
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Path.Combine(FixturesPath, fixture)
        ];

    private static int ParseChildPid(string stderrTail)
    {
        var marker = stderrTail.IndexOf("child pid=", StringComparison.Ordinal);
        marker.Should().BeGreaterThanOrEqualTo(0);
        var start = marker + "child pid=".Length;
        var end = start;
        while (end < stderrTail.Length && char.IsAsciiDigit(stderrTail[end]))
        {
            end++;
        }

        return int.Parse(stderrTail[start..end]);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(100);
        }
    }
}
