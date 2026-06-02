using System.Threading.Channels;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime.Execution;
using SelfClaw.Infrastructure.Agents.Runtime.Tools;

namespace SelfClaw.Infrastructure.Agents.Runtime.Orchestration;

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
}
