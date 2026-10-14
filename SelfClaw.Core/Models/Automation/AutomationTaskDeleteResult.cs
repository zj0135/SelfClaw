namespace SelfClaw.Core.Models;

public sealed record AutomationTaskDeleteResult(bool Deleted, string? ErrorCode, string? ErrorMessage);
