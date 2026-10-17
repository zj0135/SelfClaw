namespace SelfClaw.Core.Runtime;

/// <summary>
/// User-facing knobs of Direct context compaction. <see cref="ThresholdRatio"/> is applied to the
/// model's declared context window; <see cref="AbsoluteThresholdTokens"/> covers models that declare
/// no window. A null <see cref="ModelProfileId"/> means "follow the turn's own model".
/// </summary>
public sealed record DirectCompressionPolicy(
    bool Enabled = true,
    double ThresholdRatio = 0.75d,
    int AbsoluteThresholdTokens = 64_000,
    Guid? ModelProfileId = null);
