using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

internal sealed class DirectTurnHooksFactory
{
    private readonly CommandHookRunner _runner;
    private readonly AsyncHookExecutor _executor;
    private readonly PluginHookExecutionLog _log;
    private readonly ILogger<DirectTurnHooks> _logger;

    public DirectTurnHooksFactory(
        CommandHookRunner runner,
        AsyncHookExecutor executor,
        PluginHookExecutionLog log,
        ILogger<DirectTurnHooks>? logger = null)
    {
        _runner = runner;
        _executor = executor;
        _log = log;
        _logger = logger ?? NullLogger<DirectTurnHooks>.Instance;
    }

    public DirectTurnHooks Create(DirectHookTurnContext context, IReadOnlyList<ResolvedPluginHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(hooks);
        return new DirectTurnHooks(context, hooks, _runner.RunAsync, _executor.TryEnqueue, _log, _logger);
    }
}
