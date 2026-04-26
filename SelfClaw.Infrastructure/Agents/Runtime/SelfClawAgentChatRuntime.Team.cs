using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
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
            .OrderBy(agent => agent.SortOrder)
            .ThenBy(agent => agent.CreatedAtUtc)
            .Take(MaxTeamAgents - 1)
            .ToArray();
        var maxRounds = TeamDiscussionDefaults.ClampRounds(request.TeamMaxRounds);
        var discussionEntries = new List<DiscussionEntry>(workerAgents.Length * maxRounds);
        var failedAgentIds = new HashSet<Guid>();

        for (var roundNumber = 1; roundNumber <= maxRounds; roundNumber++)
        {
            if (failedAgentIds.Count >= workerAgents.Length)
            {
                break;
            }

            if (roundNumber > 1)
            {
                foreach (var agent in workerAgents.Where(agent => !failedAgentIds.Contains(agent.Id)))
                {
                    await writer.WriteAsync(new TeamAgentStatusChangedEvent(agent.Id, TeamAgentStatus.Ready), cancellationToken);
                }
            }

            foreach (var agent in workerAgents)
            {
                if (failedAgentIds.Contains(agent.Id))
                {
                    continue;
                }

                var entry = await RunWorkerAgentAsync(
                    request,
                    writer,
                    teamPlan.Agents,
                    agent,
                    roundNumber,
                    maxRounds,
                    discussionEntries,
                    cancellationToken);

                discussionEntries.Add(entry);
                if (!entry.Succeeded)
                {
                    failedAgentIds.Add(agent.Id);
                }
            }

            if (roundNumber < maxRounds && failedAgentIds.Count < workerAgents.Length)
            {
                var shouldContinueDiscussion = await ShouldContinueTeamDiscussionAsync(
                    request,
                    discussionEntries,
                    roundNumber,
                    maxRounds,
                    cancellationToken);

                if (!shouldContinueDiscussion)
                {
                    break;
                }
            }
        }

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
                BuildCoordinatorSummaryInstructions(request.WorkspaceRoot, request.TeamOutputMode),
                BuildCoordinatorSummaryMessages(request, teamPlan.DocumentTitle, discussionEntries),
                [],
                CreateContextProviders()),
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

        if (await ShouldPrepareTeamDocumentAsync(
                request,
                teamPlan.DocumentTitle,
                discussionEntries,
                coordinatorResult.FinalMarkdown,
                cancellationToken))
        {
            await writer.WriteAsync(
                new TeamDocumentReadyEvent(
                    coordinatorMessageId,
                    coordinatorResult.FinalMarkdown,
                    CreateTeamDocumentPath(teamPlan.DocumentTitle)),
                cancellationToken);
        }
    }

    private async Task<DiscussionEntry> RunWorkerAgentAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        IReadOnlyList<TeamAgentRecord> plannedTeamAgents,
        TeamAgentRecord agent,
        int roundNumber,
        int maxRounds,
        IReadOnlyList<DiscussionEntry> discussionEntries,
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
                    BuildWorkerPromptMessages(
                        request.Messages,
                        plannedTeamAgents,
                        agent,
                        roundNumber,
                        maxRounds,
                        discussionEntries),
                    CreateTools(request, observer, includeWriteTools: false, includeShellTool: false),
                    CreateContextProviders()),
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
            return new DiscussionEntry(roundNumber, agent, result.FinalMarkdown, true, null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "Worker agent failed. ConversationId={ConversationId}, AgentId={AgentId}, AgentName={AgentName}, RoundNumber={RoundNumber}",
                request.ConversationId,
                agent.Id,
                agent.Name,
                roundNumber);
            var failedMessage = startedMessage with
            {
                Status = MessageStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = exception.Message
            };

            await writer.WriteAsync(new AssistantMessageCompletedEvent(failedMessage), cancellationToken);
            await writer.WriteAsync(new TeamAgentStatusChangedEvent(agent.Id, TeamAgentStatus.Failed), cancellationToken);
            return new DiscussionEntry(roundNumber, agent, string.Empty, false, exception.Message);
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
                BuildCoordinatorPlanningInstructions(
                    request.WorkspaceRoot,
                    request.TeamAgents,
                    TeamDiscussionDefaults.ClampRounds(request.TeamMaxRounds)),
                BuildCoordinatorPlanningMessages(request.Messages, request.TeamAgents),
                [],
                CreateContextProviders()),
            onTextDelta: null,
            cancellationToken);

        var blueprint = TryParseTeamPlan(planResult.FinalMarkdown);
        if (blueprint is null)
        {
            _logger.LogWarning(
                "Team plan parsing fell back to the built-in blueprint. ConversationId={ConversationId}",
                request.ConversationId);
            blueprint = BuildFallbackTeamBlueprint(request);
        }

        return MaterializeTeamPlan(request, blueprint);
    }

    private TeamPlan MaterializeTeamPlan(ChatTurnRequest request, TeamBlueprint blueprint)
    {
        var now = DateTimeOffset.UtcNow;
        var existingTeamAgents = DeduplicateTeamAgents(request.TeamAgents);
        var existingAgentsByKey = existingTeamAgents.ToDictionary(BuildAgentKey, StringComparer.OrdinalIgnoreCase);
        var plannedAgents = new List<TeamAgentRecord>();
        var consumedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var coordinator = existingTeamAgents.FirstOrDefault(agent =>
            string.Equals(agent.Role, CoordinatorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agent.Name, CoordinatorName, StringComparison.OrdinalIgnoreCase));

        coordinator = coordinator is null
            ? new TeamAgentRecord(
                Guid.NewGuid(),
                request.ConversationId,
                CoordinatorName,
                CoordinatorRole,
                BuildCoordinatorSummaryInstructions(request.WorkspaceRoot, request.TeamOutputMode),
                TeamAgentStatus.Ready,
                0,
                now,
                now)
            : coordinator with
            {
                GoalPrompt = BuildCoordinatorSummaryInstructions(request.WorkspaceRoot, request.TeamOutputMode),
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

        foreach (var existing in existingTeamAgents
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

    private ExecutionPlan MaterializeExecutionPlan(ChatTurnRequest request, ExecutionPlanBlueprint blueprint)
    {
        var normalizedSteps = blueprint.Steps
            .Select(step => step with
            {
                Title = SanitizeExecutionPlanText(step.Title) ?? string.Empty
            })
            .Where(step => !string.IsNullOrWhiteSpace(step.Title))
            .Take(MaxExecutionPlanSteps)
            .ToArray();
        if (normalizedSteps.Length < MinExecutionPlanSteps)
        {
            return BuildFallbackExecutionPlan(request, blueprint.Summary);
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = normalizedSteps
            .Select((step, index) => new ExecutionPlanStep(
                NormalizeExecutionPlanStepId(step.Id, step.Title, index + 1, usedIds),
                step.Title.Trim(),
                ExecutionPlanStepStatus.Pending))
            .ToArray();

        return new ExecutionPlan(
            BuildExecutionPlanSummary(request, blueprint.Summary),
            steps);
    }

    private static ExecutionPlan BuildFallbackExecutionPlan(ChatTurnRequest request, string? rawSummary)
    {
        ExecutionPlanStep[] steps = request.WorkspaceRoot is null
            ?
            [
                new ExecutionPlanStep("clarify-scope", "Clarify requirements and constraints"),
                new ExecutionPlanStep("work-through-solution", "Perform core analysis or implementation work"),
                new ExecutionPlanStep("prepare-answer", "Summarize the outcome for the user")
            ]
            :
            [
                new ExecutionPlanStep("inspect-workspace", "Inspect the workspace and confirm entry points"),
                new ExecutionPlanStep("execute-core-work", "Execute key checks or code changes"),
                new ExecutionPlanStep("prepare-answer", "Summarize the outcome for the user")
            ];

        return new ExecutionPlan(BuildExecutionPlanSummary(request, rawSummary), steps);
    }

    private static IReadOnlyList<TeamAgentRecord> DeduplicateTeamAgents(IReadOnlyList<TeamAgentRecord> teamAgents)
        => teamAgents
            .GroupBy(BuildAgentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(agent => agent.SortOrder)
                .ThenByDescending(agent => agent.UpdatedAtUtc)
                .ThenBy(agent => agent.CreatedAtUtc)
                .First())
            .ToArray();


    private async Task<bool> ShouldPrepareTeamDocumentAsync(
        ChatTurnRequest request,
        string documentTitle,
        IReadOnlyList<DiscussionEntry> discussionEntries,
        string finalMarkdown,
        CancellationToken cancellationToken)
    {
        if (request.WorkspaceRoot is null)
        {
            return false;
        }

        if (request.TeamOutputMode == TeamOutputMode.ReplyOnly)
        {
            return false;
        }

        if (request.TeamOutputMode == TeamOutputMode.AlwaysDocument)
        {
            return true;
        }

        var decisionResult = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                CoordinatorName,
                CoordinatorDescription,
                BuildDocumentDecisionInstructions(),
                BuildDocumentDecisionMessages(request.Messages, documentTitle, discussionEntries, finalMarkdown),
                []),
            onTextDelta: null,
            cancellationToken);

        return TryParseDocumentDecision(decisionResult.FinalMarkdown)?.ShouldExportDocument ?? false;
    }


    private async Task<bool> ShouldContinueTeamDiscussionAsync(
        ChatTurnRequest request,
        IReadOnlyList<DiscussionEntry> discussionEntries,
        int currentRound,
        int maxRounds,
        CancellationToken cancellationToken)
    {
        if (currentRound >= maxRounds)
        {
            return false;
        }

        var decisionResult = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                CoordinatorName,
                CoordinatorDescription,
                BuildRoundContinuationDecisionInstructions(currentRound, maxRounds),
                BuildRoundContinuationDecisionMessages(request.Messages, discussionEntries, currentRound, maxRounds),
                []),
            onTextDelta: null,
            cancellationToken);

        return TryParseRoundContinuationDecision(decisionResult.FinalMarkdown)?.ContinueDiscussion ?? false;
    }

}
