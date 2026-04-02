using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Infrastructure.Agents;

public sealed class SelfClawAgentChatRuntime : IAgentChatRuntime
{
    private const string AgentName = "SelfClaw";
    private const string AgentDescription = "A personal desktop AI client for focused conversation and workspace assistance.";
    private const string BaseInstructions = "You are SelfClaw, a concise desktop AI assistant. Respond in Markdown. Use workspace tools when they materially help. Never claim to have read, written, or executed anything unless a tool actually returned a successful result.";

    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public SelfClawAgentChatRuntime(
        IWorkspaceToolService workspaceToolService,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _workspaceToolService = workspaceToolService;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
    }

    public IAsyncEnumerable<ChatRuntimeEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ChatRuntimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await ProduceAsync(request, channel.Writer, cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task ProduceAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var streamedText = new StringBuilder();
        var rawUpdates = new List<AgentResponseUpdate>();
        var toolObserver = new RuntimeToolObserver(writer, request.ConversationId);
        var tools = CreateTools(request, toolObserver);

        var chatClient = CreateChatClient(request);
        var chatOptions = new ChatOptions
        {
            ToolMode = tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None
        };

        var agent = new ChatClientAgent(
            chatClient,
            BuildInstructions(request),
            AgentName,
            AgentDescription,
            tools,
            _loggerFactory,
            _serviceProvider);

        var messages = request.Messages
            .Where(ShouldIncludeInPrompt)
            .Select(MapMessage)
            .ToArray();

        await foreach (var update in agent.RunStreamingAsync(messages, null, new ChatClientAgentRunOptions(chatOptions), cancellationToken))
        {
            rawUpdates.Add(update);

            var deltaText = ExtractText(update);
            if (string.IsNullOrWhiteSpace(deltaText))
            {
                continue;
            }

            streamedText.Append(deltaText);
            await writer.WriteAsync(new AssistantDeltaEvent(deltaText), cancellationToken);
        }

        stopwatch.Stop();

        var finalMarkdown = ResolveFinalMarkdown(streamedText.ToString(), rawUpdates);
        await writer.WriteAsync(
            new ChatRuntimeCompletedEvent(finalMarkdown, null, null, stopwatch.Elapsed),
            cancellationToken);
    }

    private static string ResolveFinalMarkdown(string streamedText, IReadOnlyList<AgentResponseUpdate> rawUpdates)
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

    private static string ExtractText(AgentResponseUpdate update)
    {
        if (update.Contents is not null && update.Contents.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                        builder.Append(textContent.Text);
                        break;
                    default:
                        var reasoningText = TryExtractReasoningText(content);
                        if (!string.IsNullOrWhiteSpace(reasoningText))
                        {
                            builder.Append("<think>");
                            builder.Append(reasoningText);
                            builder.Append("</think>");
                        }
                        break;
                }
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        return string.IsNullOrWhiteSpace(update.Text)
            ? string.Empty
            : update.Text;
    }

    private static bool ShouldIncludeInPrompt(MessageRecord message)
    {
        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            if (string.IsNullOrWhiteSpace(segments.ContentMarkdown))
            {
                return false;
            }
        }

        return message.Status != MessageStatus.Failed;
    }

    private static Microsoft.Extensions.AI.ChatMessage MapMessage(MessageRecord message)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;

        return new(MapRole(message.Role), content);
    }

    private static ChatRole MapRole(MessageRole role)
        => role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            _ => ChatRole.Assistant
        };

    private static IChatClient CreateChatClient(ChatTurnRequest request)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(request.Profile.Endpoint)
        };

        var client = new OpenAIChatClient(request.Profile.Model, new ApiKeyCredential(request.ApiKey), options);
        return client.AsIChatClient();
    }

    private static string BuildInstructions(ChatTurnRequest request)
    {
        if (request.WorkspaceRoot is null)
        {
            return BaseInstructions + " No workspace is currently selected, so do not mention workspace tools.";
        }

        var permissionInstructions = request.ToolPermissionMode == ToolPermissionMode.FullAccess
            ? " You may use file-writing and PowerShell tools without extra approval, but stay scoped to the selected workspace unless the user explicitly requests otherwise."
            : " File-writing and PowerShell tools require explicit user approval. Only call them when they are necessary, and keep commands narrowly scoped.";

        return BaseInstructions +
               $" The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Keep file references relative to that root." +
               permissionInstructions;
    }

    private IList<AITool> CreateTools(ChatTurnRequest request, RuntimeToolObserver observer)
    {
        if (request.WorkspaceRoot is null)
        {
            return [];
        }

        var functions = new WorkspaceToolFunctions(
            request.WorkspaceRoot,
            _workspaceToolService,
            observer,
            request.ToolPermissionMode,
            request.ToolApprovalHandler);

        return
        [
            AIFunctionFactory.Create(
                (Func<string?, CancellationToken, Task<IReadOnlyList<WorkspaceFileEntry>>>)functions.ListWorkspaceFilesAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "list_workspace_files",
                    Description = "List files and directories under the selected workspace root or under a relative directory inside it."
                }),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<IReadOnlyList<WorkspaceSearchHit>>>)functions.SearchWorkspaceTextAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "search_workspace_text",
                    Description = "Search the selected workspace for text and return matching file paths with line numbers."
                }),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<WorkspaceFileContent>>)functions.ReadWorkspaceFileAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "read_workspace_file",
                    Description = "Read a text file from the selected workspace root using a relative path."
                }),
            AIFunctionFactory.Create(
                (Func<string, string, CancellationToken, Task<WorkspaceFileWriteResult>>)functions.WriteWorkspaceFileAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "write_workspace_file",
                    Description = "Create or overwrite a UTF-8 text file inside the selected workspace root using a relative path."
                }),
            AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<ShellCommandResult>>)functions.RunShellCommandAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "run_shell_command",
                    Description = "Run a PowerShell command in the selected workspace root. Use this for inspections, build steps, or other shell-based tasks."
                })
        ];
    }

    private static string? TryExtractReasoningText(object content)
    {
        var contentType = content.GetType();
        if (!contentType.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Name.Contains("Think", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var property = contentType.GetProperties()
            .FirstOrDefault(item =>
                item.CanRead &&
                item.PropertyType == typeof(string) &&
                (item.Name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Contains("Content", StringComparison.OrdinalIgnoreCase)));

        return property?.GetValue(content) as string;
    }
}
