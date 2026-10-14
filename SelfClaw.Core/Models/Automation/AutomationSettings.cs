namespace SelfClaw.Core.Models;

/// <summary>
/// User preferences for the automation module. Failures always notify; success notifications are opt-in
/// because a daily task would otherwise produce a toast every day.
/// </summary>
public sealed record AutomationSettings(bool NotifyOnSuccess = false);
