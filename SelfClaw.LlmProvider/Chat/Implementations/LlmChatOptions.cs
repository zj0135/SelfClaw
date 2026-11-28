using System.Text.Json;
using Microsoft.Extensions.AI;


using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Protocols.Dtos;

namespace SelfClaw.LlmProvider.Chat.Implementations;

/// <summary>所有协议共享的 ChatOptions 构建：采样、输出上限、工具绑定。</summary>
internal static class LlmChatOptions
{
    /// <summary>模型选项里声明单回合输出上限的键，各协议都认。</summary>
    internal const string MaxOutputTokensKey = "max_output_tokens";

    /// <summary>模型选项里声明上下文窗口的键。本库不上线，只供调用方判断是否压缩。</summary>
    internal const string ContextWindowTokensKey = "context_window_tokens";

    /// <summary>模型选项里钉住分词器编码的键。本库不上线。</summary>
    internal const string TokenizerEncodingKey = "tokenizer.encoding";

    private const string CatalogMaxOutputTokensKey = "display.maxOutputTokens";
    private const string DisplayMetadataPrefix = "display.";

    internal static ChatOptions Create(LlmProtocolContext context, Func<IChatClient, object?>? rawFactory)
    {
        var tools = context.Options.Tools ?? [];
        return new ChatOptions
        {
            Temperature = context.Model.Sampling.Temperature is { } temperature ? (float)temperature : null,
            TopP = context.Model.Sampling.TopP is { } topP ? (float)topP : null,
            MaxOutputTokens = ResolveMaxOutputTokens(context.Model, ReadConfiguredMaxOutputTokens(context.Model)),
            ToolMode = tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = tools.Count > 0 ? tools.ToList() : null,
            RawRepresentationFactory = rawFactory
        };
    }

    /// <summary>
    /// 输出上限：类型化配置优先，其次模型选项，最后用模型清单报告的目录上限。
    /// 留空会把决定权交给 provider，而有些 provider 的默认值低到会把普通回答中途截断。
    /// </summary>
    internal static int? ResolveMaxOutputTokens(LlmModel model, int? configured)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Limits.MaxOutputTokens is int typed && typed > 0)
        {
            return typed;
        }

        if (configured > 0)
        {
            return configured;
        }

        return model.Options is { } options &&
               options.TryGetValue(CatalogMaxOutputTokensKey, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out var catalog) &&
               catalog > 0
            ? catalog
            : null;
    }

    internal static int? ReadConfiguredMaxOutputTokens(LlmModel model, string key = MaxOutputTokensKey) =>
        model.Options is { } options &&
        options.TryGetValue(key, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var tokens)
            ? tokens
            : null;

    /// <summary>
    /// 属于宿主自己的选项键：模型清单写入的展示元数据，加上窗口与分词器两个进程内键。
    /// 它们永远不上线，所以不能报成"未知线参数"。
    /// </summary>
    internal static bool IsHostOwnedOption(string key) =>
        key.StartsWith(DisplayMetadataPrefix, StringComparison.Ordinal) ||
        key is ContextWindowTokensKey or TokenizerEncodingKey;
}
