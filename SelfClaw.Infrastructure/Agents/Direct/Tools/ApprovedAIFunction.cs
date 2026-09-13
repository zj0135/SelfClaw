using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

internal sealed class ApprovedAIFunction : DelegatingAIFunction
{
    internal const string DeniedResult = "User denied this tool call.";
    private readonly Guid _conversationId;
    private readonly ToolPermissionMode _permissionMode;
    private readonly IToolApprovalHandler? _approvalHandler;
    private readonly string _displayName;
    private readonly ToolSourceKind _sourceKind;
    private readonly string? _sourceId;
    private readonly string? _transportSummary;
    private readonly string? _annotationsJson;
    private readonly bool _requiresApproval;
    private readonly IToolExecutionCheckpoint? _checkpoint;

    public ApprovedAIFunction(
        AIFunction innerFunction,
        Guid conversationId,
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        string displayName,
        ToolSourceKind sourceKind = ToolSourceKind.BuiltIn,
        string? sourceId = null,
        string? transportSummary = null,
        string? annotationsJson = null,
        bool requiresApproval = true,
        IToolExecutionCheckpoint? checkpoint = null)
        : base(innerFunction)
    {
        _conversationId = conversationId;
        _permissionMode = permissionMode;
        _approvalHandler = approvalHandler;
        _displayName = displayName;
        _sourceKind = sourceKind;
        _sourceId = sourceId;
        _transportSummary = transportSummary;
        _annotationsJson = annotationsJson;
        _requiresApproval = requiresApproval;
        _checkpoint = checkpoint;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (_requiresApproval && _permissionMode != ToolPermissionMode.FullAccess)
        {
            if (_approvalHandler is null ||
                !await _approvalHandler.RequestApprovalAsync(
                        new ToolApprovalRequest(
                            Guid.NewGuid(),
                            Name,
                            _displayName,
                            Description,
                            JsonSerializer.Serialize(arguments, JsonSerializerOptions),
                            _conversationId,
                            _sourceKind,
                            _sourceId,
                            _transportSummary,
                            _annotationsJson),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new DirectToolResult(ToolCallStatus.Canceled, DeniedResult,
                    JsonSerializer.SerializeToElement(DeniedResult), DeniedResult);
            }
        }

        if (_checkpoint is not null)
        {
            await _checkpoint.BeforeExecutionAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
