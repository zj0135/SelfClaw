using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record DirectToolBinding(
    AIFunction Tool,
    DirectToolDescriptor Descriptor,
    bool RequiresApproval = false,
    string? TransportSummary = null,
    string? AnnotationsJson = null);
