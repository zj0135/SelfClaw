using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Protocols.Dtos;

namespace SelfClaw.LlmProvider.Protocols.AnthropicMessages;

/// <summary>
/// Anthropic Messages 协议，走官方 SDK 的 IChatClient 集成。协议内的思考与输出上限是强类型的，
/// 因此这条协议暂时不接受厂商专有字段——参数表只在 OpenAI 族协议上生效。
/// </summary>
internal sealed class AnthropicMessagesEngine : ILlmProtocolEngine
{
    private const string MaxTokensKey = "max_tokens";
    private const int DefaultMaxTokens = 4096;

    public string Protocol => LlmProtocols.AnthropicMessages;

    public IChatClient CreateChatClient(LlmProtocolContext context, HttpClient httpClient)
    {
        var options = new ClientOptions
        {
            ApiKey = context.Connection.Credential.Value,
            AuthToken = null,
            BaseUrl = context.Connection.Endpoint.AbsoluteUri,
            HttpClient = httpClient
        };

        return new AnthropicClient(options).AsIChatClient(context.Model.ModelId, ResolveMaxOutputTokens(context.Model));
    }

    public ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools)
    {
        var options = LlmChatOptions.Create(context, _ => CreateReasoningOptions(context.Model));
        options.MaxOutputTokens = ResolveMaxOutputTokens(context.Model);
        return options;
    }

    /// <summary>
    /// SDK 自带 4096 的默认上限，会把普通回答截断，所以这里总是给一个解析过的值。
    /// </summary>
    private static int? ResolveMaxOutputTokens(LlmModel model)
    {
        var configured = LlmChatOptions.ReadConfiguredMaxOutputTokens(model, MaxTokensKey)
            ?? LlmChatOptions.ReadConfiguredMaxOutputTokens(model);
        return LlmChatOptions.ResolveMaxOutputTokens(model, configured);
    }

    private static MessageCreateParams? CreateReasoningOptions(LlmModel model)
    {
        if (model.ReasoningEffort is not { Length: > 0 } effort)
        {
            return null;
        }

        return new MessageCreateParams
        {
            Model = model.ModelId,
            Messages = [],
            MaxTokens = ResolveMaxOutputTokens(model) ?? DefaultMaxTokens,
            Thinking = effort == "none" ? new ThinkingConfigDisabled() : new ThinkingConfigAdaptive(),
            OutputConfig = effort == "none"
                ? null
                : new OutputConfig
                {
                    Effort = effort switch
                    {
                        "minimal" or "low" => Effort.Low,
                        "medium" => Effort.Medium,
                        "high" => Effort.High,
                        _ => Effort.Max
                    }
                }
        };
    }
}
