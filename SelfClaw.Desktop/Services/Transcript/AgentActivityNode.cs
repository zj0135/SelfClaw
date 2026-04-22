using System.Collections.Generic;

namespace SelfClaw.Desktop.Services;

public sealed record AgentActivityDetail(
    string Label,
    string Value,
    bool IsCode = false);

public sealed record AgentActivityNode(
    string Id,
    string Kind,
    string KindLabel,
    string Status,
    string StatusLabel,
    string Title,
    string Summary,
    string Timestamp,
    IReadOnlyList<AgentActivityDetail> Details,
    string? OwnerAgentId = null);
