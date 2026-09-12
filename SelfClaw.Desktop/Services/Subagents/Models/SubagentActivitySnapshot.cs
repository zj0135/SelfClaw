using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentActivitySnapshot(
    SubagentActivityDetail Detail,
    SubagentTaskActivity Activity,
    string ContentOrigin,
    long ObservationRevision);
