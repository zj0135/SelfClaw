using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class DirectTurnHooksTests
{
    [Fact]
    public async Task RunStarting_runs_hooks_in_the_given_order()
    {
        var order = new List<string>();
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.RunStarting)
            ],
            Runner(_ => HookTestFactory.Success(""), order));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        order.Should().Equal("a", "b");
        outcome.BlockedBy.Should().BeNull();
        outcome.Context.Should().BeEmpty();
    }

    [Fact]
    public async Task RunStarting_short_circuits_on_block()
    {
        var order = new List<string>();
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.RunStarting)
            ],
            Runner(
                element => HookTestFactory.Success(
                    element.GetProperty("hookId").GetString() == "a"
                        ? """{"decision":"block","reason":"nope"}"""
                        : ""),
                order));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        order.Should().Equal("a");
        outcome.BlockedBy.Should().Be(new HookSource("alpha", "a"));
        outcome.BlockReason.Should().Be("Blocked by hook 'alpha/a': nope");
    }

    [Fact]
    public async Task RunStarting_collects_context_and_notices()
    {
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting)],
            Runner(_ => HookTestFactory.Success("""{"additionalContext":"remember this"}""")));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        outcome.Context.Should().ContainSingle().Which.Text.Should().Be("remember this");
        outcome.Context[0].Source.Should().Be(new HookSource("alpha", "a"));
        outcome.Notices.Should().Equal("Hook 'alpha/a' added context (13 chars).");
    }

    [Fact]
    public async Task RunStarting_drops_context_beyond_the_turn_limit()
    {
        var hooks = HookTestFactory.Create(
            Enumerable.Range(0, 5)
                .Select(index => HookTestFactory.CreateHook(
                    $"p{index}", "h", PluginHookEvent.RunStarting, declarationOrder: index))
                .ToArray(),
            Runner(_ => HookTestFactory.Success(
                JsonSerializer.Serialize(new { additionalContext = new string('x', 16 * 1024) }))));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        outcome.Context.Should().HaveCount(4);
        outcome.Notices.Should().Contain("Hook 'p4/h' context was dropped because the turn context limit (64 KiB) was reached.");
    }

    [Fact]
    public async Task RunStarting_fails_a_hook_whose_context_exceeds_the_section_limit()
    {
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting)],
            Runner(_ => HookTestFactory.Success(
                JsonSerializer.Serialize(new { additionalContext = new string('x', 16 * 1024 + 1) }))));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        outcome.Context.Should().BeEmpty();
        outcome.Notices.Should().Contain("Hook 'alpha/a' failed (limitExceeded); ignored.");
    }

    [Fact]
    public async Task RunStarting_block_policy_turns_a_failure_into_a_block()
    {
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook(
                    "alpha", "a", PluginHookEvent.RunStarting,
                    onFailure: PluginHookFailurePolicy.Block)
            ],
            Runner(_ => HookTestFactory.Failure(HookProcessExit.LaunchFailed, detail: "missing")));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        outcome.BlockedBy.Should().Be(new HookSource("alpha", "a"));
        outcome.BlockReason.Should().Contain("failed (launchFailed) and is configured to block");
    }

    [Fact]
    public async Task RunStarting_continue_policy_ignores_a_failure_with_a_notice()
    {
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting)],
            Runner(_ => HookTestFactory.Failure(HookProcessExit.TimedOut)));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        outcome.BlockedBy.Should().BeNull();
        outcome.Notices.Should().Contain("Hook 'alpha/a' failed (timedOut); ignored.");
    }

    [Fact]
    public async Task RunStarting_skips_hooks_the_matcher_rejects_without_running_them()
    {
        var order = new List<string>();
        var matcher = new PluginHookMatcher([DirectTurnOrigin.Subagent], [], [], [], [], []);
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunStarting, matcher)],
            Runner(_ => HookTestFactory.Success(""), order));

        var outcome = await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);

        order.Should().BeEmpty();
        outcome.Notices.Should().BeEmpty();
    }

    [Fact]
    public void RunCompleted_is_not_delivered_before_runStarting_started()
    {
        var enqueued = new List<string>();
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunCompleted)],
            enqueue: work =>
            {
                enqueued.Add(work.EventName);
                return true;
            });

        hooks.RunCompleted(CompletedInput("succeeded"));

        enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCompleted_is_delivered_exactly_once()
    {
        var enqueued = new List<string>();
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.RunCompleted)],
            enqueue: work =>
            {
                enqueued.Add(work.EventName);
                return true;
            });

        await hooks.RunStartingAsync(StartingInput(), CancellationToken.None);
        hooks.RunCompleted(CompletedInput("succeeded"));
        hooks.RunCompleted(CompletedInput("failed"));

        enqueued.Should().Equal("runCompleted");
    }

    [Fact]
    public async Task ToolExecuting_chains_argument_rewrites_and_records_every_modifier()
    {
        var seenArguments = new List<JsonElement>();
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuting),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.ToolExecuting)
            ],
            Runner(element =>
            {
                lock (seenArguments)
                {
                    seenArguments.Add(element.GetProperty("arguments").Clone());
                }

                return element.GetProperty("hookId").GetString() switch
                {
                    "a" => HookTestFactory.Success("""{"updatedArguments":{"value":"first"}}"""),
                    _ => HookTestFactory.Success("""{"updatedArguments":{"value":"second"}}""")
                };
            }));

        var outcome = await hooks.ToolExecutingAsync(Call(), CancellationToken.None);

        seenArguments[0].GetProperty("value").GetString().Should().Be("original");
        seenArguments[1].GetProperty("value").GetString().Should().Be("first");
        outcome.EffectiveArguments!["value"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("second");
        outcome.EffectiveArgumentsJson.Should().Contain("second");
        outcome.ArgumentsModifiedBy.Should().Equal(new HookSource("alpha", "a"), new HookSource("beta", "b"));
    }

    [Fact]
    public async Task ToolExecuting_collects_ask_from_multiple_hooks()
    {
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuting),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.ToolExecuting)
            ],
            Runner(_ => HookTestFactory.Success("""{"decision":"ask","reason":"needs review"}""")));

        var outcome = await hooks.ToolExecutingAsync(Call(), CancellationToken.None);

        outcome.ApprovalRequiredBy.Should().HaveCount(2);
        outcome.ApprovalReason.Should().Be("needs review");
        outcome.BlockedBy.Should().BeNull();
    }

    [Fact]
    public async Task ToolExecuting_deny_short_circuits()
    {
        var order = new List<string>();
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuting),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.ToolExecuting)
            ],
            Runner(
                element => HookTestFactory.Success(
                    element.GetProperty("hookId").GetString() == "a" ? """{"decision":"deny"}""" : ""),
                order));

        var outcome = await hooks.ToolExecutingAsync(Call(), CancellationToken.None);

        order.Should().Equal("a");
        outcome.BlockedBy.Should().Be(new HookSource("alpha", "a"));
        outcome.BlockReason.Should().Be("Blocked by hook 'alpha/a': No reason given.");
    }

    [Fact]
    public async Task ToolExecuting_rejects_a_rewrite_that_fails_schema_validation()
    {
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuting)],
            Runner(_ => HookTestFactory.Success("""{"updatedArguments":{"value":1}}""")));

        var outcome = await hooks.ToolExecutingAsync(Call(), CancellationToken.None);

        outcome.EffectiveArguments.Should().BeNull();
        outcome.IgnoredFailures.Should().ContainSingle().Which.Kind.Should().Be("argumentsInvalid");
    }

    [Fact]
    public async Task ToolExecuted_collects_feedback_and_limits_it()
    {
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuted),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.ToolExecuted)
            ],
            Runner(element => HookTestFactory.Success(
                JsonSerializer.Serialize(new
                {
                    feedback = element.GetProperty("hookId").GetString() == "a"
                        ? new string('a', 8 * 1024 + 1)
                        : "useful"
                }))));

        var outcome = await hooks.ToolExecutedAsync(Call(), ExecutedResult(), CancellationToken.None);

        outcome.Feedback.Should().ContainSingle().Which.Text.Should().Be("useful");
        outcome.IgnoredFailures.Should().ContainSingle().Which.Kind.Should().Be("limitExceeded");
    }

    [Fact]
    public async Task ToolExecuted_enqueues_async_hooks_without_waiting()
    {
        var enqueued = new List<AsyncHookWork>();
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.ToolExecuted, runAsync: true)],
            enqueue: work =>
            {
                enqueued.Add(work);
                return true;
            });

        var outcome = await hooks.ToolExecutedAsync(Call(), ExecutedResult(), CancellationToken.None);

        enqueued.Should().ContainSingle().Which.EventName.Should().Be("toolExecuted");
        outcome.Feedback.Should().BeEmpty();
    }

    [Fact]
    public void HttpHooks_are_detected_and_body_wanted_only_for_matching_hooks()
    {
        var matcher = new PluginHookMatcher([], [], [], [], [], ["api.example.com"]);
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook(
                    "alpha", "a", PluginHookEvent.HttpRequestSending, matcher, includeRequestBody: true)
            ]);

        hooks.HasHttpHooks.Should().BeTrue();
        hooks.WantsRequestBody("api.example.com").Should().BeTrue();
        hooks.WantsRequestBody("other.example.com").Should().BeFalse();
    }

    [Fact]
    public async Task HttpRequestSending_merges_headers_first_write_wins()
    {
        var hooks = HookTestFactory.Create(
            [
                HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.HttpRequestSending),
                HookTestFactory.CreateHook("beta", "b", PluginHookEvent.HttpRequestSending)
            ],
            Runner(element => HookTestFactory.Success(
                element.GetProperty("hookId").GetString() == "a"
                    ? """{"addHeaders":{"x-trace":"first"}}"""
                    : """{"addHeaders":{"x-trace":"second","x-other":"value"}}""")));

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1?secret=1");
        var outcome = await hooks.HttpRequestSendingAsync(
            new HttpHookRequest(1, request, null, false),
            CancellationToken.None);

        outcome.Headers.Should().Equal(
            new KeyValuePair<string, string>("x-trace", "first"),
            new KeyValuePair<string, string>("x-other", "value"));
    }

    [Fact]
    public async Task HttpRequestSending_never_exposes_the_query_or_a_secret_header_value()
    {
        JsonElement? payload = null;
        var hooks = HookTestFactory.Create(
            [HookTestFactory.CreateHook("alpha", "a", PluginHookEvent.HttpRequestSending)],
            (_, bytes, _, _) =>
            {
                payload = JsonDocument.Parse(bytes).RootElement.Clone();
                return Task.FromResult(HookTestFactory.Success(""));
            });
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1?token=secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");

        await hooks.HttpRequestSendingAsync(new HttpHookRequest(1, request, null, false), CancellationToken.None);

        var value = payload!.Value;
        value.GetProperty("url").GetString().Should().Be("https://api.example.com/v1");
        value.GetProperty("queryParameterNames").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("token");
        value.GetProperty("headers").GetProperty("authorization").GetString().Should().Be("[redacted]");
        value.GetRawText().Should().NotContain("Bearer secret");
        value.GetRawText().Should().NotContain("token=secret");
    }

    private static Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> Runner(
        Func<JsonElement, HookProcessResult> respond,
        List<string>? order = null)
        => (_, payload, _, _) =>
        {
            using var document = JsonDocument.Parse(payload);
            if (order is not null)
            {
                lock (order)
                {
                    order.Add(document.RootElement.GetProperty("hookId").GetString()!);
                }
            }

            return Task.FromResult(respond(document.RootElement));
        };

    private static RunStartingInput StartingInput()
        => new(
            "Test Provider",
            AiProviderKind.OpenAI,
            "test-model",
            "prompt",
            [],
            ["read_file"],
            null);

    private static RunCompletedInput CompletedInput(string status)
        => new(status, "final", null, 1, 2, 3, TimeSpan.FromSeconds(1));

    private static ToolHookCall Call()
    {
        var function = AIFunctionFactory.Create((string value) => value, "read_file", "Reads a file.");
        var binding = new DirectToolBinding(
            function,
            new DirectToolDescriptor("read_file", ToolCallKind.Read));
        return new ToolHookCall(
            "call-1",
            0,
            binding,
            new AIFunctionArguments { ["value"] = "original" },
            ToolPermissionMode.RequireApproval);
    }

    private static ToolHookResult ExecutedResult()
        => new(
            ToolCallStatus.Completed,
            DeniedBy: null,
            Summary: "read",
            Content: JsonSerializer.SerializeToElement("body"),
            Error: null,
            EffectiveArgumentsJson: null,
            Duration: TimeSpan.FromMilliseconds(5));
}
