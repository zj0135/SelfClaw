namespace SelfClaw.LlmProvider.Profiles.Dtos;

/// <summary>
/// 模型声明的能力上限。全部可选：null 表示模型没有声明，本库不据此做任何裁剪。
/// </summary>
/// <param name="ContextWindowTokens">上下文窗口；调用方用它判断是否需要压缩。</param>
/// <param name="MaxOutputTokens">输出上限；优先于模型选项里的同名键。</param>
/// <param name="IsMultimodal">是否接受图片输入，也决定工具结果能否携带图片。</param>
public readonly record struct LlmModelLimits(
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null,
    bool IsMultimodal = false);
