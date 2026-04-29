using System.ClientModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using SelfClaw.Core.Models;
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
                AIContextProviders = request.ContextProviders ?? []
            };
            var agent = new ChatClientAgent(
                CreateChatClient(request.Profile, request.ApiKey),
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
