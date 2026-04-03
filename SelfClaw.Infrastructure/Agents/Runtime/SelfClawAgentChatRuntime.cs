using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Infrastructure.Agents;

public sealed partial class SelfClawAgentChatRuntime : IAgentChatRuntime
{
    private const int MaxTeamAgents = 5;
    private const int MaxParallelTeamAgents = 3;
    private const string ProgrammingAgentName = "SelfClaw";
    private const string ProgrammingAgentRole = "Programming Assistant";
    private const string ProgrammingAgentDescription = "A personal desktop AI client for focused conversation and workspace assistance.";
    private const string ProgrammingBaseInstructions = "You are SelfClaw, a concise desktop AI assistant. Respond in Markdown. Use workspace tools when they materially help. Never claim to have read, written, or executed anything unless a tool actually returned a successful result.";
    private const string CoordinatorName = "Coordinator";
    private const string CoordinatorRole = "Coordinator";
    private const string CoordinatorDescription = "A coordination agent that designs a multi-agent review flow and synthesizes final team output.";

    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly IAgentExecutionService _agentExecutionService;

    public SelfClawAgentChatRuntime(
        IWorkspaceToolService workspaceToolService,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _workspaceToolService = workspaceToolService;
        _agentExecutionService = new ChatClientAgentExecutionService(loggerFactory, serviceProvider);
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
                if (request.Mode == ConversationMode.Team)
                {
                    await ProduceTeamTurnAsync(request, channel.Writer, cancellationToken);
                }
                else
                {
                    await ProduceProgrammingTurnAsync(request, channel.Writer, cancellationToken);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public static string ExtractTextFromContents(IList<AIContent>? contents)
        => ChatClientAgentExecutionService.ExtractTextFromContents(contents);

    private async Task ProduceProgrammingTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agentId: null,
            ProgrammingAgentName,
            ProgrammingAgentRole);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        var observer = new RuntimeToolObserver(writer, request.ConversationId, null, messageId);
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                ProgrammingAgentName,
                ProgrammingAgentDescription,
                BuildProgrammingInstructions(request),
                BuildPromptMessages(request.Messages),
                CreateTools(request, observer, includeWriteTools: true, includeShellTool: true)),
            (delta, token) => writer.WriteAsync(new AssistantDeltaEvent(messageId, delta), token),
            cancellationToken);

        await writer.WriteAsync(
            new AssistantMessageCompletedEvent(startedMessage with
            {
                MarkdownContent = result.FinalMarkdown,
                Status = MessageStatus.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                DurationMs = result.Duration.TotalMilliseconds,
                ErrorMessage = null
            }),
            cancellationToken);
    }

    private async Task ProduceTeamTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var teamPlan = await PlanTeamAsync(request, cancellationToken);
        await writer.WriteAsync(new TeamAgentsPlannedEvent(teamPlan.Agents), cancellationToken);

        var coordinator = teamPlan.Coordinator;
        var workerAgents = teamPlan.Agents
            .Where(agent => agent.Id != coordinator.Id)
            .Take(MaxTeamAgents - 1)
            .ToArray();

        var workerOutputs = new List<WorkerOutput>(workerAgents.Length);
        using var concurrencyGate = new SemaphoreSlim(MaxParallelTeamAgents);
        var workerTasks = workerAgents.Select(async agent =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                var output = await RunWorkerAgentAsync(request, writer, agent, cancellationToken);
                lock (workerOutputs)
                {
                    workerOutputs.Add(output);
                }
            }
            finally
            {
                concurrencyGate.Release();
            }
        }).ToArray();

        await Task.WhenAll(workerTasks);

        await writer.WriteAsync(new TeamAgentStatusChangedEvent(coordinator.Id, TeamAgentStatus.Running), cancellationToken);

        var coordinatorMessageId = Guid.NewGuid();
        var coordinatorMessage = CreateAssistantMessage(
            request.ConversationId,
            coordinatorMessageId,
            coordinator.Id,
            coordinator.Name,
            coordinator.Role);

        await writer.WriteAsync(new AssistantMessageStartedEvent(coordinatorMessage), cancellationToken);

        var coordinatorResult = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                coordinator.Name,
                CoordinatorDescription,
                coordinator.GoalPrompt,
                BuildCoordinatorSummaryMessages(request, teamPlan.DocumentTitle, workerOutputs),
                []),
            (delta, token) => writer.WriteAsync(new AssistantDeltaEvent(coordinatorMessageId, delta), token),
            cancellationToken);

        var completedCoordinatorMessage = coordinatorMessage with
        {
            MarkdownContent = coordinatorResult.FinalMarkdown,
            Status = MessageStatus.Completed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            InputTokens = coordinatorResult.InputTokens,
            OutputTokens = coordinatorResult.OutputTokens,
            DurationMs = coordinatorResult.Duration.TotalMilliseconds,
            ErrorMessage = null
        };

        await writer.WriteAsync(new AssistantMessageCompletedEvent(completedCoordinatorMessage), cancellationToken);
        await writer.WriteAsync(new TeamAgentStatusChangedEvent(coordinator.Id, TeamAgentStatus.Completed), cancellationToken);
        await writer.WriteAsync(
            new TeamDocumentReadyEvent(
                coordinatorMessageId,
                coordinatorResult.FinalMarkdown,
                CreateTeamDocumentPath(teamPlan.DocumentTitle)),
            cancellationToken);
    }

    private async Task<WorkerOutput> RunWorkerAgentAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        TeamAgentRecord agent,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(new TeamAgentStatusChangedEvent(agent.Id, TeamAgentStatus.Running), cancellationToken);

        var messageId = Guid.NewGuid();
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agent.Id,
            agent.Name,
            agent.Role);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        try
        {
            var observer = new RuntimeToolObserver(writer, request.ConversationId, agent.Id, messageId);
            var result = await _agentExecutionService.RunAsync(
                new AgentExecutionRequest(
                    request.Profile,
                    request.ApiKey,
                    agent.Name,
                    $"Team specialist: {agent.Role}",
                    agent.GoalPrompt,
                    BuildWorkerPromptMessages(request.Messages, request.TeamAgents, agent),
                    CreateTools(request, observer, includeWriteTools: false, includeShellTool: false)),
                (delta, token) => writer.WriteAsync(new AssistantDeltaEvent(messageId, delta), token),
                cancellationToken);

            var completedMessage = startedMessage with
            {
                MarkdownContent = result.FinalMarkdown,
                Status = MessageStatus.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                DurationMs = result.Duration.TotalMilliseconds,
                ErrorMessage = null
            };

            await writer.WriteAsync(new AssistantMessageCompletedEvent(completedMessage), cancellationToken);
            await writer.WriteAsync(new TeamAgentStatusChangedEvent(agent.Id, TeamAgentStatus.Completed), cancellationToken);
            return new WorkerOutput(agent, result.FinalMarkdown, true, null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failedMessage = startedMessage with
            {
                Status = MessageStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = exception.Message
            };

            await writer.WriteAsync(new AssistantMessageCompletedEvent(failedMessage), cancellationToken);
            await writer.WriteAsync(new TeamAgentStatusChangedEvent(agent.Id, TeamAgentStatus.Failed), cancellationToken);
            return new WorkerOutput(agent, string.Empty, false, exception.Message);
        }
    }

    private async Task<TeamPlan> PlanTeamAsync(ChatTurnRequest request, CancellationToken cancellationToken)
    {
        var planResult = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                CoordinatorName,
                CoordinatorDescription,
                BuildCoordinatorPlanningInstructions(request.WorkspaceRoot, request.TeamAgents),
                BuildCoordinatorPlanningMessages(request.Messages, request.TeamAgents),
                []),
            onTextDelta: null,
            cancellationToken);

        var blueprint = TryParseTeamPlan(planResult.FinalMarkdown) ?? BuildFallbackTeamBlueprint(request);
        return MaterializeTeamPlan(request, blueprint);
    }

    private TeamPlan MaterializeTeamPlan(ChatTurnRequest request, TeamBlueprint blueprint)
    {
        var now = DateTimeOffset.UtcNow;
        var existingAgentsByKey = request.TeamAgents.ToDictionary(BuildAgentKey, StringComparer.OrdinalIgnoreCase);
        var plannedAgents = new List<TeamAgentRecord>();
        var consumedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var coordinator = request.TeamAgents.FirstOrDefault(agent =>
            string.Equals(agent.Role, CoordinatorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agent.Name, CoordinatorName, StringComparison.OrdinalIgnoreCase));

        coordinator = coordinator is null
            ? new TeamAgentRecord(
                Guid.NewGuid(),
                request.ConversationId,
                CoordinatorName,
                CoordinatorRole,
                BuildCoordinatorSummaryInstructions(request.WorkspaceRoot),
                TeamAgentStatus.Ready,
                0,
                now,
                now)
            : coordinator with
            {
                GoalPrompt = BuildCoordinatorSummaryInstructions(request.WorkspaceRoot),
                Status = TeamAgentStatus.Ready,
                SortOrder = 0,
                UpdatedAtUtc = now
            };

        plannedAgents.Add(coordinator);
        consumedKeys.Add(BuildAgentKey(coordinator.Name, coordinator.Role));

        var sortOrder = 1;
        foreach (var member in blueprint.Agents.Take(MaxTeamAgents - 1))
        {
            var key = BuildAgentKey(member.Name, member.Role);
            var goalPrompt = BuildWorkerInstructions(member, request.WorkspaceRoot);
            if (!existingAgentsByKey.TryGetValue(key, out var existing))
            {
                existing = new TeamAgentRecord(
                    Guid.NewGuid(),
                    request.ConversationId,
                    member.Name,
                    member.Role,
                    goalPrompt,
                    TeamAgentStatus.Ready,
                    sortOrder,
                    now,
                    now);
            }
            else
            {
                existing = existing with
                {
                    GoalPrompt = goalPrompt,
                    Status = TeamAgentStatus.Ready,
                    SortOrder = sortOrder,
                    UpdatedAtUtc = now
                };
            }

            plannedAgents.Add(existing);
            consumedKeys.Add(key);
            sortOrder++;
        }

        foreach (var existing in request.TeamAgents
                     .OrderBy(agent => agent.SortOrder)
                     .ThenBy(agent => agent.CreatedAtUtc)
                     .Where(agent => !consumedKeys.Contains(BuildAgentKey(agent))))
        {
            if (plannedAgents.Count >= MaxTeamAgents)
            {
                break;
            }

            plannedAgents.Add(existing with
            {
                Status = TeamAgentStatus.Ready,
                SortOrder = sortOrder,
                UpdatedAtUtc = now
            });
            sortOrder++;
        }

        return new TeamPlan(
            plannedAgents,
            coordinator,
            string.IsNullOrWhiteSpace(blueprint.DocumentTitle)
                ? CreateDocumentTitleFromMessages(request.Messages)
                : blueprint.DocumentTitle.Trim());
    }

    private static IReadOnlyList<ChatMessage> BuildPromptMessages(IReadOnlyList<MessageRecord> messages)
        => messages
            .Where(ShouldIncludeInPrompt)
            .Select(MapMessage)
            .ToArray();

    private static IReadOnlyList<ChatMessage> BuildCoordinatorPlanningMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<TeamAgentRecord> existingTeamAgents)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(messages));
        if (existingTeamAgents.Count > 0)
        {
            promptMessages.Add(new ChatMessage(
                ChatRole.System,
                "Current active team roster:\n" + string.Join(
                    "\n",
                    existingTeamAgents.OrderBy(agent => agent.SortOrder)
                        .Select(agent => $"- {agent.Name} ({agent.Role})"))));
        }

        return promptMessages;
    }

    private static IReadOnlyList<ChatMessage> BuildWorkerPromptMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<TeamAgentRecord> existingTeamAgents,
        TeamAgentRecord currentAgent)
    {
        var roster = existingTeamAgents.Count == 0
            ? $"Current team roster:\n- {currentAgent.Name} ({currentAgent.Role})"
            : "Current team roster:\n" + string.Join(
                "\n",
                existingTeamAgents.OrderBy(agent => agent.SortOrder)
                    .Select(agent => $"- {agent.Name} ({agent.Role})"));

        var promptMessages = new List<ChatMessage>
        {
            new(ChatRole.System, roster),
            new(ChatRole.System,
                $"You are contributing as {currentAgent.Name} ({currentAgent.Role}). Do not write the final consolidated document; focus on your specialty and cite assumptions and risks explicitly.")
        };

        promptMessages.AddRange(BuildPromptMessages(messages));
        return promptMessages;
    }

    private static IReadOnlyList<ChatMessage> BuildCoordinatorSummaryMessages(
        ChatTurnRequest request,
        string documentTitle,
        IReadOnlyList<WorkerOutput> workerOutputs)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(request.Messages));
        var report = new StringBuilder();
        report.AppendLine($"Target document title: {documentTitle}");
        report.AppendLine("Team findings:");

        foreach (var output in workerOutputs.OrderBy(item => item.Agent.SortOrder))
        {
            report.AppendLine();
            report.AppendLine($"## {output.Agent.Name} ({output.Agent.Role})");
            if (!output.Succeeded)
            {
                report.AppendLine("Status: failed");
                report.AppendLine($"Reason: {output.ErrorMessage}");
                continue;
            }

            report.AppendLine(output.Markdown);
        }

        promptMessages.Add(new ChatMessage(ChatRole.System, report.ToString()));
        if (request.WorkspaceRoot is null)
        {
            promptMessages.Add(new ChatMessage(
                ChatRole.System,
                "No workspace is selected. Mention clearly that the final Markdown stays in chat only and no file was written."));
        }

        return promptMessages;
    }

    private static MessageRecord CreateAssistantMessage(
        Guid conversationId,
        Guid messageId,
        Guid? agentId,
        string agentName,
        string agentRole)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            messageId,
            conversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            agentId,
            agentName,
            agentRole);
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

    private static ChatMessage MapMessage(MessageRecord message)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;

        if (message.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.AgentName))
        {
            var speaker = string.IsNullOrWhiteSpace(message.AgentRole)
                ? message.AgentName
                : $"{message.AgentName} ({message.AgentRole})";
            content = $"[{speaker}]\n{content}";
        }

        return new ChatMessage(MapRole(message.Role), content);
    }

    private static ChatRole MapRole(MessageRole role)
        => role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            _ => ChatRole.Assistant
        };

    private static string BuildProgrammingInstructions(ChatTurnRequest request)
    {
        if (request.WorkspaceRoot is null)
        {
            return ProgrammingBaseInstructions + " No workspace is currently selected, so do not mention workspace tools.";
        }

        var permissionInstructions = request.ToolPermissionMode == ToolPermissionMode.FullAccess
            ? " You may use file-writing and PowerShell tools without extra approval, but stay scoped to the selected workspace unless the user explicitly requests otherwise."
            : " File-writing and PowerShell tools require explicit user approval. Only call them when they are necessary, and keep commands narrowly scoped.";

        return ProgrammingBaseInstructions +
               $" The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Keep file references relative to that root." +
               permissionInstructions;
    }

    private static string BuildCoordinatorPlanningInstructions(
        WorkspaceRoot? workspaceRoot,
        IReadOnlyList<TeamAgentRecord> existingTeamAgents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the coordinator for a Windows desktop AI product.");
        builder.AppendLine("Your job is to choose a compact team of specialists for requirement discussion and solution design.");
        builder.AppendLine("Return JSON only. Do not use Markdown fences.");
        builder.AppendLine("Schema:");
        builder.AppendLine("{\"documentTitle\":\"string\",\"agents\":[{\"name\":\"string\",\"role\":\"string\",\"mission\":\"string\"}]}");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Team size limit is 5 total members including the coordinator.");
        builder.AppendLine("- Prefer specialists like PM, architect, DBA, backend, security, frontend only when relevant.");
        builder.AppendLine("- Prefer reusing the existing team when it already covers the task.");
        builder.AppendLine("- Each mission should be one concise sentence focused on analysis, not coding.");
        if (workspaceRoot is null)
        {
            builder.AppendLine("- No workspace is selected, so plan for discussion and documentation only.");
        }
        else
        {
            builder.AppendLine($"- The trusted workspace root is '{workspaceRoot.RootPath}'. Specialists may inspect it read-only.");
        }

        if (existingTeamAgents.Count > 0)
        {
            builder.AppendLine("- Existing team members are already available; only add new specialties when there is a gap.");
        }

        return builder.ToString();
    }

    private static string BuildCoordinatorSummaryInstructions(WorkspaceRoot? workspaceRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the main coordinator in a team discussion workflow.");
        builder.AppendLine("Write the final answer in Markdown as a design document suitable for saving to a .md file.");
        builder.AppendLine("Structure the document with: title, background, requirements, proposed design, data model, key flows, risks, open questions, and implementation guidance.");
        builder.AppendLine("Synthesize and de-duplicate specialist feedback. Preserve conflicts as explicit decisions or open questions.");
        builder.AppendLine("Mention assumptions and unknowns clearly.");
        if (workspaceRoot is null)
        {
            builder.AppendLine("Make it explicit that no workspace was selected, so the document remains in chat unless the user later selects one.");
        }

        return builder.ToString();
    }

    private static string BuildWorkerInstructions(TeamBlueprintAgent agent, WorkspaceRoot? workspaceRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You are {agent.Name}, acting as {agent.Role}.");
        builder.AppendLine(agent.Mission);
        builder.AppendLine("Respond in Markdown.");
        builder.AppendLine("Focus on your specialty, surface assumptions, identify risks, and suggest concrete design choices.");
        builder.AppendLine("Do not write the final consolidated answer. Do not claim tools were used unless tool results were actually returned.");
        if (workspaceRoot is null)
        {
            builder.AppendLine("No workspace is selected, so work from the task description and prior discussion only.");
        }
        else
        {
            builder.AppendLine($"The trusted workspace root is '{workspaceRoot.RootPath}'. You may inspect it read-only via list/search/read tools when helpful.");
        }

        return builder.ToString();
    }

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

    private static TeamBlueprint? TryParseTeamPlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var blueprint = JsonSerializer.Deserialize<TeamBlueprint>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (blueprint is null || blueprint.Agents.Count == 0)
            {
                return null;
            }

            blueprint = blueprint with
            {
                Agents = blueprint.Agents
                    .Where(agent =>
                        !string.IsNullOrWhiteSpace(agent.Name) &&
                        !string.IsNullOrWhiteSpace(agent.Role) &&
                        !string.IsNullOrWhiteSpace(agent.Mission))
                    .Take(MaxTeamAgents - 1)
                    .ToArray()
            };

            return blueprint.Agents.Count == 0 ? null : blueprint;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var fenced = Regex.Match(raw, "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
        {
            return fenced.Groups[1].Value;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static TeamBlueprint BuildFallbackTeamBlueprint(ChatTurnRequest request)
    {
        var latestPrompt = request.Messages.LastOrDefault(message => message.Role == MessageRole.User)?.MarkdownContent ?? "Design discussion";
        var agents = new List<TeamBlueprintAgent>
        {
            new("Product Manager", "Requirements", "Clarify business goals, user scope, and acceptance criteria for the requested design."),
            new("Solution Architect", "Architecture", "Define the overall architecture, module boundaries, and technical trade-offs."),
            new("Security Specialist", "Security", "Review security boundaries, privilege rules, auditing, and abuse risks.")
        };

        if (latestPrompt.Contains("permissions", StringComparison.OrdinalIgnoreCase) ||
            latestPrompt.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            latestPrompt.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            agents.Add(new TeamBlueprintAgent("DBA", "Data Model", "Design tables, indexes, and data constraints needed to support the proposal."));
        }

        return new TeamBlueprint(CreateDocumentTitleFromMessages(request.Messages), agents.Take(MaxTeamAgents - 1).ToArray());
    }

    private static string CreateDocumentTitleFromMessages(IReadOnlyList<MessageRecord> messages)
    {
        var latestPrompt = messages.LastOrDefault(message => message.Role == MessageRole.User)?.MarkdownContent ?? "Team Summary";
        var firstLine = latestPrompt.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Team Summary";
        }

        return firstLine.Length > 36 ? firstLine[..36].Trim() : firstLine;
    }

    private static string CreateTeamDocumentPath(string documentTitle)
    {
        var slug = Slugify(documentTitle);
        return $"docs/selfclaw-team/{slug}.md";
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "team-summary";
        }

        var normalized = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousDash = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
                continue;
            }

            if (previousDash)
            {
                continue;
            }

            builder.Append('-');
            previousDash = true;
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "team-summary" : slug;
    }

    private static string BuildAgentKey(TeamAgentRecord agent)
        => BuildAgentKey(agent.Name, agent.Role);

    private static string BuildAgentKey(string name, string role)
        => $"{name.Trim()}::{role.Trim()}";

    private sealed record TeamPlan(
        IReadOnlyList<TeamAgentRecord> Agents,
        TeamAgentRecord Coordinator,
        string DocumentTitle);

    private sealed record TeamBlueprint(
        string DocumentTitle,
        IReadOnlyList<TeamBlueprintAgent> Agents);

    private sealed record TeamBlueprintAgent(
        string Name,
        string Role,
        string Mission);

    private sealed record WorkerOutput(
        TeamAgentRecord Agent,
        string Markdown,
        bool Succeeded,
        string? ErrorMessage);
}
