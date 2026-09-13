using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;

internal sealed record McpServerResolution(
    McpServerConfigRecord Server,
    bool Connected,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Degradations,
    bool HealthRecorded,
    IReadOnlyList<DirectToolBinding> Tools);
