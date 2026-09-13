using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentActivitySnapshot(
    SubagentTaskActivity Activity,
    SubagentContentSnapshot Content,
    string ContentOrigin);
