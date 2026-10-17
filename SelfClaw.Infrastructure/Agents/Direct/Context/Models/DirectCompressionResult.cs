namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// Model is the wire model that actually produced the summary. UsedFallbackModel tells the runtime to
// emit a diagnostic because the policy's model was unavailable. SummaryTokens is counted by
// IDirectTokenCounter against the producing model's profile; null means counting was unavailable.
internal sealed record DirectCompressionResult(
    string Summary,
    string? Model,
    int SourceMessageCount,
    int? SourceTokens,
    int? SummaryTokens,
    bool UsedFallbackModel = false);
