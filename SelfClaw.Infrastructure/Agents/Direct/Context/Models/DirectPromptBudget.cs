namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// The declared window bounds the whole prompt; the output cap reserves space for the response. Neither
// value is a trimmable allowance: history is compacted, never silently dropped.
internal readonly record struct DirectPromptBudget(int? ContextWindowTokens, int? MaxOutputTokens);
