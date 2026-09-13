using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record DirectToolDescriptor(
    string ProviderName,
    ToolCallKind Kind,
    ToolSourceKind? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null);
