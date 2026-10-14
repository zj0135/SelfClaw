namespace SelfClaw.Core.Models;

public sealed record AutomationHistoryReclaimResult(int DeletedRuns, string? ErrorCode, string? ErrorMessage);
