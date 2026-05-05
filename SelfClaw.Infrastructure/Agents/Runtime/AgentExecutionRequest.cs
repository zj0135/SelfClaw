using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed record AgentExecutionRequest(
    ProviderProfile Profile,
    string ApiKey,
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<ChatMessage> Messages,
    IList<AITool> Tools,
    IReadOnlyList<AIContextProvider>? ContextProviders = null,
    bool EnableReasoning = false,
    RuntimeToolObserver? ToolObserver = null,
    ToolPermissionMode ToolPermissionMode = ToolPermissionMode.FullAccess,
    IToolApprovalHandler? ToolApprovalHandler = null,
    IReadOnlyDictionary<string, ToolInvocationMetadata>? ToolMetadata = null);
