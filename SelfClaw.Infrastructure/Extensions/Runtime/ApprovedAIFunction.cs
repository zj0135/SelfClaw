using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

internal sealed class ApprovedAIFunction : DelegatingAIFunction
{
    private readonly Guid _conversationId;
    private readonly ToolPermissionMode _permissionMode;
    private readonly IToolApprovalHandler? _approvalHandler;
    private readonly string _displayName;
    private readonly ToolSourceKind _sourceKind;
    private readonly string? _sourceId;
    private readonly string? _transportSummary;
    private readonly string? _annotationsJson;
    private readonly Func<object?, object?>? _transformResult;

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
        Func<object?, object?>? transformResult = null)
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
        _transformResult = transformResult;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (_permissionMode != ToolPermissionMode.FullAccess)
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
                return JsonSerializer.SerializeToElement(WorkspaceAgentToolset.DeniedResult);
            }
        }

        var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        return _transformResult is null ? result : _transformResult(result);
    }
}
