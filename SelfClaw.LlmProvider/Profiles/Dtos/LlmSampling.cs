namespace SelfClaw.LlmProvider.Profiles.Dtos;

/// <summary>采样参数。null 表示不发送该字段，由 provider 用自身默认值。</summary>
public readonly record struct LlmSampling(double? Temperature = null, double? TopP = null);
