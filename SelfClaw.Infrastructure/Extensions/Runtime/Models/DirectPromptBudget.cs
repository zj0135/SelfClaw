namespace SelfClaw.Infrastructure.Extensions.Runtime.Models;

// An unset context window preserves full history; the output cap reserves space for the response.
internal readonly record struct DirectPromptBudget(int? ContextWindowTokens, int? MaxOutputTokens);
