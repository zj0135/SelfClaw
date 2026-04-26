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
                BuildPromptMessages(request.Messages, includeAssistantSpeakerPrefix: false),
                CreateTools(request, observer, includeWriteTools: true, includeShellTool: true),
                CreateContextProviders()),
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
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                ProgrammingAgentName,
                ProgrammingAgentDescription,
                BuildExecutionPlanInstructions(request),
                BuildExecutionPlanMessages(request.Messages),
                CreateTools(request, observer, includeWriteTools: false, includeShellTool: false),
                CreateContextProviders()),
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
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agentId: null,
            ProgrammingAgentName,
            ProgrammingAgentRole);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        var observer = new RuntimeToolObserver(writer, request.ConversationId, null, messageId);
        try
        {
            var result = await _agentExecutionService.RunAsync(
                new AgentExecutionRequest(
                    request.Profile,
                    request.ApiKey,
                    ProgrammingAgentName,
                    ProgrammingAgentDescription,
                    BuildExecutionStepInstructions(request, executionPlan, currentStep, isFinalStep),
                    BuildExecutionStepMessages(request.Messages, executionPlan, currentStep, completedSteps, isFinalStep),
                    CreateTools(request, observer, includeWriteTools: true, includeShellTool: true),
                    CreateContextProviders()),
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

    private async Task ProduceBoundAgentTurnAsync(
        ChatTurnRequest request,
        ChannelWriter<ChatRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var agent = request.BoundAgent!;
        var messageId = Guid.NewGuid();
        var startedMessage = CreateAssistantMessage(
            request.ConversationId,
            messageId,
            agent.Id,
            agent.Name,
            agent.Role);

        await writer.WriteAsync(new AssistantMessageStartedEvent(startedMessage), cancellationToken);

        var observer = new RuntimeToolObserver(writer, request.ConversationId, agent.Id, messageId);
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                request.Profile,
                request.ApiKey,
                agent.Name,
                BoundAgentSessionDescription,
                BuildBoundAgentInstructions(request, agent),
                BuildBoundAgentPromptMessages(request.ContextMessages ?? [], request.Messages, agent),
                CreateTools(request, observer, includeWriteTools: true, includeShellTool: true),
                CreateContextProviders()),
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

}
