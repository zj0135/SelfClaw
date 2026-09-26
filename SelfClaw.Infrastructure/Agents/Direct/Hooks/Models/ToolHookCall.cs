using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolHookCall(
    string CallId,
    int Iteration,
    DirectToolBinding Binding,
    AIFunctionArguments Arguments,
    ToolPermissionMode PermissionMode);
