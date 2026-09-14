using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Agents.Models;

internal sealed record AgentEdit(string Id, string Name, string Description, AgentExecutionMode Mode, string Instructions);
