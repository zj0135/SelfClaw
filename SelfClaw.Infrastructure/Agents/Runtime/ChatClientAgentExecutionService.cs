using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class ChatClientAgentExecutionService : IAgentExecutionService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChatClientAgentExecutionService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ChatClientAgentExecutionService(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChatClientAgentExecutionService>();
        _serviceProvider = serviceProvider;
    }

    public async Task<AgentExecutionResult> RunAsync(
        AgentExecutionRequest request,
        Func<string, CancellationToken, ValueTask>? onTextDelta,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var streamedText = new StringBuilder();
        var rawUpdates = new List<AgentResponseUpdate>();

        try
        {
            var chatOptions = new ChatOptions
            {
                Temperature = request.Profile.TemperatureEnabled ? (float)request.Profile.Temperature : null,
                TopP = request.Profile.TopPEnabled ? (float)request.Profile.TopP : null,
                ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
                Tools = request.Tools
            };

            if (request.EnableReasoning)
            {
                chatOptions.RawRepresentationFactory = _ =>
                {
#pragma warning disable SCME0001
                    OpenAI.Chat.ChatCompletionOptions completionOptions = new();
                    completionOptions.Patch.Set("$.thinking.type"u8, "enabled");
                    return completionOptions;
#pragma warning restore SCME0001
                };
            }

            var agentOptions = new ChatClientAgentOptions
            {
                Name = request.Name,
                Description = request.Description,
                ChatOptions = chatOptions,
                AIContextProviders = request.ContextProviders ?? [],
                UseProvidedChatClientAsIs = true
            };
            var chatClient = CreateConfiguredChatClient(request);
            var agent = new ChatClientAgent(
                chatClient,
                agentOptions,
                _loggerFactory,
                _serviceProvider);
            var promptMessages = PrependInstructions(request.Instructions, request.Messages);

            await foreach (var update in agent.RunStreamingAsync(
                               promptMessages,
                               null,
                               null,
                               cancellationToken))
            {
                rawUpdates.Add(update);

                var deltaText = ExtractText(update);
                if (string.IsNullOrWhiteSpace(deltaText))
                {
                    continue;
                }

                streamedText.Append(deltaText);
                if (onTextDelta is not null)
                {
                    await onTextDelta(deltaText, cancellationToken);
                }
            }

            stopwatch.Stop();
            var usage = ResolveUsage(rawUpdates, _logger);

            return new AgentExecutionResult(
                ResolveFinalMarkdown(streamedText.ToString(), rawUpdates, _logger),
                ToStoredTokenCount(usage?.InputTokenCount),
                ToStoredTokenCount(usage?.OutputTokenCount),
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Agent execution was canceled. AgentName={AgentName}, ProfileName={ProfileName}, Model={Model}",
                request.Name,
                request.Profile.Name,
                request.Profile.Model);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(
                exception,
                "Agent execution failed. AgentName={AgentName}, ProfileName={ProfileName}, Model={Model}",
                request.Name,
                request.Profile.Name,
                request.Profile.Model);
            throw;
        }
    }

    private static IChatClient CreateChatClient(ProviderProfile profile, string apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(profile.Endpoint)
        };

        var client = new OpenAIChatClient(profile.Model, new ApiKeyCredential(apiKey), options);
        return client.AsIChatClient();
    }

    private IChatClient CreateConfiguredChatClient(AgentExecutionRequest request)
    {
        var leafClient = CreateChatClient(request.Profile, request.ApiKey);
        var functionInvokingClient = new FunctionInvokingChatClient(leafClient, _loggerFactory, _serviceProvider)
        {
            FunctionInvoker = (context, cancellationToken) => new ValueTask<object?>(
                InvokeFunctionAsync(request, context, cancellationToken))
        };

        return functionInvokingClient;
    }

    private async Task<object?> InvokeFunctionAsync(
        AgentExecutionRequest request,
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (request.ToolObserver is null ||
            request.ToolMetadata is null ||
            !request.ToolMetadata.TryGetValue(context.Function.Name, out var metadata))
        {
            return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }

        var argumentsJson = SerializeFunctionArguments(context.Arguments, context.Function.JsonSerializerOptions);
        var record = request.ToolObserver.Start(context.Function.Name, argumentsJson);

        try
        {
            if (metadata.RequiresApproval)
            {
                if (request.ToolApprovalHandler is null)
                {
                    throw new InvalidOperationException("This tool call requires human approval, but no approval handler is available.");
                }

                record = request.ToolObserver.AwaitApproval(record, "Waiting for your confirmation in the activity panel.");
                var approved = await request.ToolApprovalHandler.RequestApprovalAsync(
                    new ToolApprovalRequest(
                        record.Id,
                        context.Function.Name,
                        metadata.ApprovalTitle,
                        metadata.BuildApprovalDescription(argumentsJson),
                        argumentsJson,
                        record.ConversationId),
                    cancellationToken);
                if (!approved)
                {
                    var deniedResult = metadata.BuildDeniedResult(argumentsJson);
                    request.ToolObserver.Cancel(record, metadata.SummarizeResult(deniedResult, context.Function.JsonSerializerOptions));
                    return deniedResult;
                }

                record = request.ToolObserver.Resume(
                    record,
                    metadata.BuildApprovalGrantedSummary(argumentsJson));
            }

            var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
            var summary = metadata.SummarizeResult(result, context.Function.JsonSerializerOptions);
            var description = metadata.DescribeResult(result, context.Function.JsonSerializerOptions);
            if (string.IsNullOrWhiteSpace(description))
            {
                request.ToolObserver.Complete(record, summary);
            }
            else
            {
                request.ToolObserver.Complete(record, summary, description);
            }

            return result;
        }
        catch (Exception exception)
        {
            request.ToolObserver.Fail(record, exception.Message);
            throw;
        }
    }

    private static string SerializeFunctionArguments(
        AIFunctionArguments arguments,
        JsonSerializerOptions serializerOptions)
    {
        if (arguments.Count == 0)
        {
            return "{}";
        }

        try
        {
            var values = arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return JsonSerializer.Serialize(values, serializerOptions);
        }
        catch
        {
            return "{}";
        }
    }

    private static IReadOnlyList<ChatMessage> PrependInstructions(string instructions, IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return messages;
        }

        var promptMessages = new List<ChatMessage>(messages.Count + 1)
        {
            new(ChatRole.System, instructions)
        };
        promptMessages.AddRange(messages);
        return promptMessages;
    }

    public static string ResolveFinalMarkdown(
        string streamedText,
        IReadOnlyList<AgentResponseUpdate> rawUpdates,
        ILogger? logger = null)
    {
        if (rawUpdates.Count == 0)
        {
            return streamedText;
        }

        string? aggregatedText;
        try
        {
            aggregatedText = rawUpdates.ToAgentResponse().Text;
        }
        catch (Exception exception)
        {
            // Some providers may emit malformed partial JSON in streaming metadata (for example
            // incomplete function-call arguments near token limits). If aggregation fails, keep
            // the already-streamed text instead of failing the entire turn.
            logger?.LogWarning(exception, "Failed to aggregate streamed agent response metadata; falling back to streamed text.");
            return streamedText;
        }

        if (string.IsNullOrWhiteSpace(streamedText))
        {
            return aggregatedText ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(aggregatedText) && aggregatedText.Length > streamedText.Length)
        {
            return aggregatedText;
        }

        return streamedText;
    }

    private static UsageDetails? ResolveUsage(
        IReadOnlyList<AgentResponseUpdate> rawUpdates,
        ILogger? logger = null)
    {
        if (rawUpdates.Count == 0)
        {
            return null;
        }

        try
        {
            return rawUpdates.ToAgentResponse().Usage;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Failed to aggregate streamed agent usage metadata.");
            return null;
        }
    }

    private static int? ToStoredTokenCount(long? value)
    {
        if (value is null or < 0)
        {
            return null;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }

    public static string ExtractText(AgentResponseUpdate update)
    {
        var contentText = ExtractTextFromContents(update.Contents);
        if (!string.IsNullOrWhiteSpace(contentText))
        {
            return contentText;
        }

        return string.IsNullOrWhiteSpace(update.Text)
            ? string.Empty
            : update.Text;
    }

    public static string ExtractTextFromContents(IList<AIContent>? contents)
    {
        if (contents is null || contents.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    builder.Append(textContent.Text);
                    break;
                case TextReasoningContent reasoningContent when !string.IsNullOrWhiteSpace(reasoningContent.Text):
                    builder.Append(AssistantMessageSegmenter.WrapThinking(reasoningContent.Text));
                    break;
            }
        }

        return builder.ToString();
    }
}
