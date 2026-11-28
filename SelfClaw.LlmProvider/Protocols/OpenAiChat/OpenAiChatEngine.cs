using Microsoft.Extensions.AI;
using OpenAI.Chat;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OpenAIClientOptions = OpenAI.OpenAIClientOptions;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable SCME0001

namespace SelfClaw.LlmProvider.Protocols.OpenAiChat;

/// <summary>OpenAI Chat Completions 协议。</summary>
internal sealed class OpenAiChatEngine : ILlmProtocolEngine
{
    public string Protocol => LlmProtocols.OpenAiChat;

    public IChatClient CreateChatClient(LlmProtocolContext context, HttpClient httpClient)
    {
        var client = new OpenAIChatClient(
            context.Model.ModelId,
            new ApiKeyCredential(context.Connection.Credential.Value),
            CreateClientOptions(context, httpClient));
        return new OpenAiChatReasoningReplayClient(client.AsIChatClient());
    }

    public ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools)
    {
        var options = LlmChatOptions.Create(context, _ => BuildRawOptions(context));
        // 这个格式没有对应的 raw 补丁：M.E.AI 会把 MaxOutputTokens 映射成 max_completion_tokens，
        // 所以设在这里才是让服务端默认值不截断回答的地方。
        options.MaxOutputTokens = LlmChatOptions.ResolveMaxOutputTokens(
            context.Model,
            LlmChatOptions.ReadConfiguredMaxOutputTokens(context.Model));
        return options;
    }

    private static OpenAIClientOptions CreateClientOptions(LlmProtocolContext context, HttpClient httpClient) =>
        new()
        {
            Endpoint = context.Connection.Endpoint,
            Transport = new HttpClientPipelineTransport(httpClient)
        };

    private static OpenAIChatCompletionOptions BuildRawOptions(LlmProtocolContext context)
    {
        var raw = new OpenAIChatCompletionOptions();
        OpenAiChatRawPatch.Apply(raw, context.Parameters);
        return raw;
    }
}

#pragma warning restore SCME0001
