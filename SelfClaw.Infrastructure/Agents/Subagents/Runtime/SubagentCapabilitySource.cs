using System.ComponentModel;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal sealed class SubagentCapabilitySource
{
    internal const string DelegateToolName = "delegate_to_subagent";
    internal const string GetTaskToolName = "get_subagent_task";
    internal const string CancelTaskToolName = "cancel_subagent_task";
    internal const string RetryTaskToolName = "retry_subagent_task";

    private readonly ISubagentTaskCoordinator? _coordinator;

    public SubagentCapabilitySource(ISubagentTaskCoordinator? coordinator)
    {
        _coordinator = coordinator;
    }

    internal IReadOnlyList<(AITool Tool, DirectToolDescriptor Descriptor)> CreateTools(
        DirectChatTurnRequest request,
        DirectCapabilityCeiling capabilityCeiling)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilityCeiling);
        if (_coordinator is null ||
            request.ExecutionContext.Origin == DirectTurnOrigin.Subagent ||
            request.Agent.SubagentIds.Count == 0)
        {
            return [];
        }

        if (request.ModelProfileId is not Guid modelProfileId)
        {
            throw new InvalidOperationException("Subagent delegation requires a concrete parent model profile.");
        }

        var bound = new BoundTools(_coordinator, request, capabilityCeiling, modelProfileId);
        return
        [
            Create(
                AIFunctionFactory.Create(
                    bound.DelegateAsync,
                    DelegateToolName,
                    "Queue an authorized Subagent task and return immediately with its durable task state."),
                ToolCallKind.Run,
                "Delegate to Subagent"),
            Create(
                AIFunctionFactory.Create(
                    bound.GetAsync,
                    GetTaskToolName,
                    "Get the current state of a Subagent task created by this conversation."),
                ToolCallKind.Read,
                "Get Subagent task"),
            Create(
                AIFunctionFactory.Create(
                    bound.CancelAsync,
                    CancelTaskToolName,
                    "Cancel a queued or running Subagent task created by this conversation."),
                ToolCallKind.Run,
                "Cancel Subagent task"),
            Create(
                AIFunctionFactory.Create(
                    bound.RetryAsync,
                    RetryTaskToolName,
                    "Retry a terminal Subagent task as a new durable attempt."),
                ToolCallKind.Run,
                "Retry Subagent task")
        ];
    }

    private static (AITool Tool, DirectToolDescriptor Descriptor) Create(
        AITool tool,
        ToolCallKind kind,
        string displayName)
        => (tool, new DirectToolDescriptor(tool.Name, kind, ToolSourceKind.BuiltIn, DisplayName: displayName));

    private sealed class BoundTools
    {
        private readonly ISubagentTaskCoordinator _coordinator;
        private readonly DirectChatTurnRequest _request;
        private readonly DirectCapabilityCeiling _capabilityCeiling;
        private readonly Guid _modelProfileId;

        public BoundTools(
            ISubagentTaskCoordinator coordinator,
            DirectChatTurnRequest request,
            DirectCapabilityCeiling capabilityCeiling,
            Guid modelProfileId)
        {
            _coordinator = coordinator;
            _request = request;
            _capabilityCeiling = capabilityCeiling;
            _modelProfileId = modelProfileId;
        }

        public Task<SubagentTaskView> DelegateAsync(
            [Description("Exact allowlisted Subagent id.")] string subagentId,
            [Description("Complete, explicit task for the isolated Subagent. Parent history is not copied.")] string task,
            CancellationToken cancellationToken)
            => _coordinator.StartAsync(
                new SubagentTaskStartRequest(
                    _request.ConversationId,
                    _request.TurnId,
                    subagentId,
                    task,
                    _request.Agent,
                    _modelProfileId,
                    _request.WorkspaceRoot,
                    _request.ToolPermissionMode,
                    _capabilityCeiling),
                cancellationToken);

        public Task<SubagentTaskView?> GetAsync(
            [Description("Durable Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
            => _coordinator.GetAsync(
                new SubagentTaskQuery(_request.ConversationId, taskId),
                cancellationToken);

        public Task<SubagentTaskView> CancelAsync(
            [Description("Durable Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
            => _coordinator.CancelAsync(
                new SubagentTaskCommand(_request.ConversationId, taskId),
                cancellationToken);

        public Task<SubagentTaskView> RetryAsync(
            [Description("Terminal Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
            => _coordinator.RetryAsync(
                new SubagentTaskRetryRequest(_request.ConversationId, _request.TurnId, taskId),
                cancellationToken);
    }
}
