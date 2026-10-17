using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record DirectToolDescriptor(
    ToolCallKind Kind,
    ToolSourceKind? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null);
