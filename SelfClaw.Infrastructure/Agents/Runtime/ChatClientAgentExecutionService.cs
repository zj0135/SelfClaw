using System.ClientModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Infrastructure.Agents;

internal sealed class ChatClientAgentExecutionService : IAgentExecutionService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public ChatClientAgentExecutionService(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
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

        var chatOptions = new ChatOptions
        {
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None
        };

        var agent = new ChatClientAgent(
            CreateChatClient(request.Profile, request.ApiKey),
            request.Instructions,
            request.Name,
            request.Description,
            request.Tools,
            _loggerFactory,
            _serviceProvider);

        await foreach (var update in agent.RunStreamingAsync(
                           request.Messages,
                           null,
                           new ChatClientAgentRunOptions(chatOptions),
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

        return new AgentExecutionResult(
            ResolveFinalMarkdown(streamedText.ToString(), rawUpdates),
            null,
            null,
            stopwatch.Elapsed);
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

    public static string ResolveFinalMarkdown(string streamedText, IReadOnlyList<AgentResponseUpdate> rawUpdates)
    {
        if (rawUpdates.Count == 0)
        {
            return streamedText;
        }

        var aggregatedText = rawUpdates.ToAgentResponse().Text;
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
