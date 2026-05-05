using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Tools;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime : IAgentChatRuntime
{
    private const int MaxExecutionPlanSteps = 7;
    private const int MinExecutionPlanSteps = 3;
    private const string DefaultProgrammingAgentDescription = "A personal desktop AI client for focused conversation and workspace assistance.";
    private const string DefaultChannelAgentName = "SelfClaw";
    private const string DefaultChannelAgentRole = "Channel Assistant";
    private const string DefaultChannelAgentDescription = "A desktop AI client replying to external channel conversations.";
    private const string ProgrammingBaseInstructions = "You are SelfClaw, a concise desktop AI assistant. Respond in Markdown. Use workspace tools when they materially help. Never claim to have read, written, or executed anything unless a tool actually returned a successful result.";
    private const string ChannelBaseInstructions = "You are SelfClaw replying to a user from an external chat channel. Keep replies concise, helpful, and easy to read in chat. Respond in Markdown or plain text that still reads well when Markdown is not rendered. Never expose hidden reasoning, internal tools, or implementation details unless the user explicitly asks.";

    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly IAgentExecutionService _agentExecutionService;
    private readonly IAgentContextProviderFactory _agentContextProviderFactory;
    private readonly ILogger<SelfClawAgentChatRuntime> _logger;

    public SelfClawAgentChatRuntime(
        IWorkspaceToolService workspaceToolService,
        IAgentContextProviderFactory agentContextProviderFactory,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
        : this(
            workspaceToolService,
            agentContextProviderFactory,
            new ChatClientAgentExecutionService(loggerFactory, serviceProvider),
            loggerFactory.CreateLogger<SelfClawAgentChatRuntime>())
    {
    }

    internal SelfClawAgentChatRuntime(
        IWorkspaceToolService workspaceToolService,
        IAgentExecutionService agentExecutionService)
        : this(
            workspaceToolService,
            new EmptyAgentContextProviderFactory(),
            agentExecutionService,
            NullLogger<SelfClawAgentChatRuntime>.Instance)
    {
    }

    internal SelfClawAgentChatRuntime(
        IWorkspaceToolService workspaceToolService,
        IAgentContextProviderFactory agentContextProviderFactory,
        IAgentExecutionService agentExecutionService,
        ILogger<SelfClawAgentChatRuntime>? logger = null)
    {
        _workspaceToolService = workspaceToolService;
        _agentContextProviderFactory = agentContextProviderFactory;
        _agentExecutionService = agentExecutionService;
        _logger = logger ?? NullLogger<SelfClawAgentChatRuntime>.Instance;
    }

    public IAsyncEnumerable<ChatRuntimeEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ChatRuntimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (request.Mode == ConversationMode.Programming && request.Agent.Mode == AgentExecutionMode.Plan)
                {
                    await ProducePlannedProgrammingTurnAsync(request, channel.Writer, cancellationToken);
                }
                else
                {
                    await ProduceProgrammingTurnAsync(request, channel.Writer, cancellationToken);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        exception,
                        "Chat runtime failed. ConversationId={ConversationId}, Mode={Mode}, AgentId={AgentId}, AgentMode={AgentMode}",
                        request.ConversationId,
                        request.Mode,
                        request.Agent.Id,
                        request.Agent.Mode);
                }

                channel.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public static string ExtractTextFromContents(IList<AIContent>? contents)
        => ChatClientAgentExecutionService.ExtractTextFromContents(contents);

    private IList<AITool> CreateTools(
        ChatTurnRequest request,
        RuntimeToolObserver observer,
        bool includeWriteTools,
        bool includeShellTool)
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

        var tools = new List<AITool>
        {
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
                })
        };

        if (includeWriteTools)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<string, string, CancellationToken, Task<WorkspaceFileWriteResult>>)functions.WriteWorkspaceFileAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "write_workspace_file",
                    Description = "Create or overwrite a UTF-8 text file inside the selected workspace root using a relative path."
                }));
        }

        if (includeShellTool)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<ShellCommandResult>>)functions.RunShellCommandAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "run_shell_command",
                    Description = "Run a PowerShell command in the selected workspace root. Use this for inspections, build steps, or other shell-based tasks."
                }));
        }

        return tools;
    }

    private IReadOnlyList<AIContextProvider> CreateContextProviders(AgentRuntimeDefinition agent)
        => _agentContextProviderFactory.CreateProviders(agent);

    private static string ResolveAgentName(ChatTurnRequest request)
        => request.Mode == ConversationMode.Channel
            ? DefaultChannelAgentName
            : request.Agent.Name;

    private static string ResolveAgentRole(ChatTurnRequest request)
        => request.Mode == ConversationMode.Channel
            ? DefaultChannelAgentRole
            : request.Agent.Mode == AgentExecutionMode.Plan
                ? "Plan Agent"
                : "Agent";

    private static string ResolveAgentDescription(ChatTurnRequest request)
    {
        if (request.Mode == ConversationMode.Channel)
        {
            return DefaultChannelAgentDescription;
        }

        return string.IsNullOrWhiteSpace(request.Agent.Description)
            ? DefaultProgrammingAgentDescription
            : request.Agent.Description;
    }

}
