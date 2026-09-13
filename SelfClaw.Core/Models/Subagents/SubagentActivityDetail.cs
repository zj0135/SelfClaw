namespace SelfClaw.Core.Models;

public sealed record SubagentActivityDetail(
    SubagentActivityTask Task,
    SubagentContentSnapshot Content);
