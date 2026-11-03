namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// The prompt size one decision is made against: PromptTokens is reserve-free because the context readout
// reports it that way, TotalWithReserve adds the output space the response may use. Both are measured, never
// estimated; a caller that cannot count gets no measurement at all.
internal sealed record DirectPromptMeasurement(int PromptTokens, int TotalWithReserve);
