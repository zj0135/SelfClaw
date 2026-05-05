using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private async Task ProduceProgrammingTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var agentName = ResolveAgentName(request);
        var agentRole = ResolveAgentRole(request);
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agentId: null,
            agentName,
            agentRole);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        var observer = new RuntimeToolObserver(writer, request.ConversationId, null, messageId);
        await using var toolScope = await CreateToolsAsync(
            request,
            includeWriteTools: true,
            includeShellTool: true,
            cancellationToken);
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                agentName,
                ResolveAgentDescription(request),
                BuildProgrammingInstructions(request),
                BuildPromptMessages(request.Messages, includeAssistantSpeakerPrefix: false),
                toolScope.Tools,
                CreateContextProviders(request.Agent),
                request.EnableReasoning,
                observer,
                request.ToolPermissionMode,
                request.ToolApprovalHandler,
                toolScope.ToolMetadata),
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

    private async Task ProducePlannedProgrammingTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(new ExecutionPlanDraftingStartedEvent(), cancellationToken);
        var executionPlan = await DraftExecutionPlanAsync(request, writer, cancellationToken);
        await writer.WriteAsync(new ExecutionPlanPreparedEvent(executionPlan), cancellationToken);

        var completedSteps = new List<CompletedExecutionPlanStep>(executionPlan.Steps.Count);
        for (var index = 0; index < executionPlan.Steps.Count; index++)
        {
            var step = executionPlan.Steps[index];
            await writer.WriteAsync(
                new ExecutionPlanStepStatusChangedEvent(step.Id, ExecutionPlanStepStatus.Running),
                cancellationToken);

            try
            {
                var result = await ProduceExecutionPlanStepTurnAsync(
                    request,
                    writer,
                    executionPlan,
                    step,
                    completedSteps,
                    isFinalStep: index == executionPlan.Steps.Count - 1,
                    cancellationToken);

                completedSteps.Add(new CompletedExecutionPlanStep(step, result.FinalMarkdown));
                await writer.WriteAsync(
                    new ExecutionPlanStepStatusChangedEvent(step.Id, ExecutionPlanStepStatus.Completed),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await writer.WriteAsync(
                    new ExecutionPlanStepStatusChangedEvent(step.Id, ExecutionPlanStepStatus.Cancelled),
                    cancellationToken);
                throw;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                await writer.WriteAsync(
                    new ExecutionPlanStepStatusChangedEvent(step.Id, ExecutionPlanStepStatus.Failed),
                    cancellationToken);
                throw;
            }
        }
    }

    private async Task<ExecutionPlan> DraftExecutionPlanAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var observer = new RuntimeToolObserver(writer, request.ConversationId, null, messageId: null);
        await using var toolScope = await CreateToolsAsync(
            request,
            includeWriteTools: false,
            includeShellTool: false,
            cancellationToken);
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                ResolveAgentName(request),
                ResolveAgentDescription(request),
                BuildExecutionPlanInstructions(request),
                BuildExecutionPlanMessages(request.Messages),
                toolScope.Tools,
                CreateContextProviders(request.Agent),
                request.EnableReasoning,
                observer,
                request.ToolPermissionMode,
                request.ToolApprovalHandler,
                toolScope.ToolMetadata),
            onTextDelta: null,
            cancellationToken);

        var blueprint = TryParseExecutionPlan(result.FinalMarkdown);
        if (blueprint is null)
        {
            _logger.LogWarning(
                "Execution plan parsing fell back to the built-in plan. ConversationId={ConversationId}",
                request.ConversationId);
            return BuildFallbackExecutionPlan(request, result.FinalMarkdown);
        }

        return MaterializeExecutionPlan(request, blueprint);
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

    private async Task<AgentExecutionResult> ProduceExecutionPlanStepTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        ExecutionPlan executionPlan,
        ExecutionPlanStep currentStep,
        IReadOnlyList<CompletedExecutionPlanStep> completedSteps,
        bool isFinalStep,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var agentName = ResolveAgentName(request);
        var agentRole = ResolveAgentRole(request);
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agentId: null,
            agentName,
            agentRole);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        var observer = new RuntimeToolObserver(writer, request.ConversationId, null, messageId);
        await using var toolScope = await CreateToolsAsync(
            request,
            includeWriteTools: true,
            includeShellTool: true,
            cancellationToken);
        try
        {
            var result = await _agentExecutionService.RunAsync(
                new AgentExecutionRequest(
                    request.Profile,
                    request.ApiKey,
                    agentName,
                    ResolveAgentDescription(request),
                    BuildExecutionStepInstructions(request, executionPlan, currentStep, isFinalStep),
                    BuildExecutionStepMessages(request.Messages, executionPlan, currentStep, completedSteps, isFinalStep),
                    toolScope.Tools,
                    CreateContextProviders(request.Agent),
                    request.EnableReasoning,
                    observer,
                    request.ToolPermissionMode,
                    request.ToolApprovalHandler,
                    toolScope.ToolMetadata),
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

            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "Execution plan step failed. ConversationId={ConversationId}, StepId={StepId}, StepTitle={StepTitle}",
                request.ConversationId,
                currentStep.Id,
                currentStep.Title);
            await writer.WriteAsync(
                new AssistantMessageCompletedEvent(startedMessage with
                {
                    Status = MessageStatus.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = exception.Message
                }),
                cancellationToken);
            throw;
        }
    }
}
