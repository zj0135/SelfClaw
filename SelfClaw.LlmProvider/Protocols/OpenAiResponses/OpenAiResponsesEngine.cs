using Microsoft.Extensions.AI;
using OpenAI.Responses;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Parameters.Implementations;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAIResponseReasoningEffortLevel = OpenAI.Responses.ResponseReasoningEffortLevel;
using OpenAIResponseReasoningOptions = OpenAI.Responses.ResponseReasoningOptions;
using OpenAIResponseReasoningSummaryVerbosity = OpenAI.Responses.ResponseReasoningSummaryVerbosity;
using OpenAIResponseTruncationMode = OpenAI.Responses.ResponseTruncationMode;
using OpenAIResponsesClient = OpenAI.Responses.ResponsesClient;
using OpenAICreateResponseOptions = OpenAI.Responses.CreateResponseOptions;

#pragma warning disable OPENAI001

namespace SelfClaw.LlmProvider.Protocols.OpenAiResponses;

/// <summary>
/// OpenAI Responses 协议。思考只由显式模型选项驱动，绝不从"是否启用了推理"派生，
/// 否则会给不支持该参数的模型塞进非法字段。
/// </summary>
internal sealed class OpenAiResponsesEngine : ILlmProtocolEngine
{
    private const string EffortPath = "$.reasoning.effort";
    private const string SummaryPath = "$.reasoning.summary";
    private const string StorePath = "$.store";
    private const string TruncationPath = "$.truncation";
    private const string ParallelToolCallsPath = "$.parallel_tool_calls";

    public string Protocol => LlmProtocols.OpenAiResponses;

    public IChatClient CreateChatClient(LlmProtocolContext context, HttpClient httpClient)
    {
        var client = new OpenAIResponsesClient(
            new ApiKeyCredential(context.Connection.Credential.Value),
            CreateClientOptions(context, httpClient));
        return new OpenAiResponsesReplayClient(client.AsIChatClient(context.Model.ModelId));
    }

    public ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools) =>
        LlmChatOptions.Create(context, _ => BuildRawOptions(context));

    private static ResponsesClientOptions CreateClientOptions(LlmProtocolContext context, HttpClient httpClient) =>
        new()
        {
            Endpoint = context.Connection.Endpoint,
            Transport = new HttpClientPipelineTransport(httpClient)
        };

    private static OpenAICreateResponseOptions BuildRawOptions(LlmProtocolContext context)
    {
        var raw = new OpenAICreateResponseOptions();
        var parameters = context.Parameters;

        OpenAIResponseReasoningOptions? reasoning = null;
        if (LlmParameterReader.TryGetString(parameters, EffortPath, out var effort))
        {
            reasoning = new OpenAIResponseReasoningOptions
            {
                ReasoningEffortLevel = new OpenAIResponseReasoningEffortLevel(effort)
            };
        }

        if (LlmParameterReader.TryGetString(parameters, SummaryPath, out var summary))
        {
            reasoning ??= new OpenAIResponseReasoningOptions();
            reasoning.ReasoningSummaryVerbosity = new OpenAIResponseReasoningSummaryVerbosity(summary);
        }

        if (reasoning is not null)
        {
            raw.ReasoningOptions = reasoning;
        }

        if (LlmParameterReader.TryGetBool(parameters, StorePath, out var store))
        {
            raw.StoredOutputEnabled = store;
        }

        // 这个格式里 raw 值优先于 ChatOptions.MaxOutputTokens，所以目录兜底也必须在这里落地。
        if (LlmChatOptions.ResolveMaxOutputTokens(
                context.Model,
                LlmChatOptions.ReadConfiguredMaxOutputTokens(context.Model)) is { } maxOutputTokens)
        {
            raw.MaxOutputTokenCount = maxOutputTokens;
        }

        if (LlmParameterReader.TryGetString(parameters, TruncationPath, out var truncation))
        {
            raw.TruncationMode = new OpenAIResponseTruncationMode(truncation);
        }

        if (LlmParameterReader.TryGetBool(parameters, ParallelToolCallsPath, out var parallelToolCalls))
        {
            raw.ParallelToolCallsEnabled = parallelToolCalls;
        }

        return raw;
    }
}

#pragma warning restore OPENAI001
