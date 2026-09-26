using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

/// <summary>
/// Builds <see cref="DirectTurnHooks"/> around a fake process runner so the merge logic can be tested
/// without spawning processes.
/// </summary>
internal static class HookTestFactory
{
    internal static DirectHookTurnContext CreateContext(
        DirectTurnOrigin origin = DirectTurnOrigin.Interactive,
        string? workspaceRoot = null,
        string agentId = "build",
        string agentName = "Build",
        Guid? turnId = null,
        Guid? conversationId = null)
        => new(
            turnId ?? Guid.NewGuid(),
            conversationId ?? Guid.NewGuid(),
            origin,
            agentId,
            agentName,
            workspaceRoot,
            "Test Provider",
            AiProviderKind.OpenAI,
            "test-model");

    internal static ResolvedPluginHook CreateHook(
        string pluginId,
        string hookId,
        PluginHookEvent hookEvent,
        PluginHookMatcher? matcher = null,
        string? command = "hook.exe",
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        PluginHookFailurePolicy onFailure = PluginHookFailurePolicy.Continue,
        bool runAsync = false,
        bool includeRequestBody = false,
        int declarationOrder = 0,
        bool inherited = false)
        => new(
            pluginId,
            "1.0.0",
            Path.Combine(Path.GetTempPath(), pluginId),
            new PluginHookContribution(
                hookId,
                hookEvent,
                matcher ?? EmptyMatcher,
                command!,
                arguments ?? [],
                timeout ?? TimeSpan.FromSeconds(10),
                onFailure,
                runAsync,
                includeRequestBody),
            declarationOrder,
            inherited);

    internal static PluginHookMatcher EmptyMatcher { get; } = new([], [], [], [], [], []);

    internal static DirectTurnHooks Create(
        DirectHookTurnContext context,
        IReadOnlyList<ResolvedPluginHook> hooks,
        Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>>? runner = null,
        Func<AsyncHookWork, bool>? enqueue = null,
        PluginHookExecutionLog? log = null)
        => new(
            context,
            hooks,
            runner ?? ((_, _, _, _) => Task.FromResult(Success(""))),
            enqueue ?? (_ => true),
            log ?? new PluginHookExecutionLog());

    internal static DirectTurnHooks Create(
        IReadOnlyList<ResolvedPluginHook> hooks,
        Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>>? runner = null,
        Func<AsyncHookWork, bool>? enqueue = null,
        PluginHookExecutionLog? log = null)
        => Create(CreateContext(), hooks, runner, enqueue, log);

    internal static DirectTurnHooksFactory CreateFactory()
    {
        var log = new PluginHookExecutionLog();
        var commandRunner = new CommandHookRunner();
        var executor = new AsyncHookExecutor(commandRunner, log, new PluginVersionLeaseManager());
        return new DirectTurnHooksFactory(commandRunner, executor, log);
    }

    internal static DirectTurnHooksFactory CreateFactory(AsyncHookExecutor executor, PluginHookExecutionLog log)
        => new(new CommandHookRunner(), executor, log);

    internal static HookProcessResult Success(string stdout, int exitCode = 0, string stderr = "")
        => new(HookProcessExit.Exited, exitCode, stdout, stderr, TimeSpan.FromMilliseconds(1), null);

    internal static HookProcessResult Failure(HookProcessExit exit, int? exitCode = null, string? detail = null)
        => new(exit, exitCode, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1), detail);
}
