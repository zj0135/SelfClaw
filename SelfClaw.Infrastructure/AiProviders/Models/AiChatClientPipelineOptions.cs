using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Everything the per-turn chat pipeline needs beyond the resolved provider request: the bound tools,
/// the single tool-invocation seam, and an optional outermost handler for the turn's HTTP client.
/// </summary>
public sealed record AiChatClientPipelineOptions(
    IReadOnlyList<AITool> Tools,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> FunctionInvoker,
    DelegatingHandler? HttpHandler = null);
