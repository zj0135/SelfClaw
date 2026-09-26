using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The single tool-invocation seam for a Direct turn: it owns approval and the durable execution
/// checkpoint, and it is installed as the pipeline's M.E.AI <c>FunctionInvoker</c> so every tool call
/// passes through exactly once.
/// </summary>
internal sealed class DirectToolInvoker
{
    internal const string DeniedResult = "User denied this tool call.";

    private readonly DirectChatTurnRequest _request;
    private readonly IReadOnlyDictionary<string, DirectToolBinding> _bindings;

    public DirectToolInvoker(
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, DirectToolBinding> bindings)
    {
        _request = request;
        _bindings = bindings;
    }

    public async ValueTask<object?> InvokeAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var toolName = context.Function.Name;
        if (!_bindings.TryGetValue(toolName, out var binding))
        {
            throw new InvalidOperationException($"Direct tool '{toolName}' is not bound to this turn.");
        }

        if (binding.RequiresApproval && _request.ToolPermissionMode != ToolPermissionMode.FullAccess)
        {
            if (_request.ToolApprovalHandler is null ||
                !await _request.ToolApprovalHandler.RequestApprovalAsync(
                        new ToolApprovalRequest(
                            Guid.NewGuid(),
                            toolName,
                            binding.Descriptor.DisplayName ?? toolName,
                            binding.Tool.Description,
                            JsonSerializer.Serialize(context.Arguments, context.Function.JsonSerializerOptions),
                            _request.ConversationId,
                            binding.Descriptor.SourceKind ?? ToolSourceKind.BuiltIn,
                            binding.Descriptor.SourceId,
                            binding.TransportSummary,
                            binding.AnnotationsJson),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new DirectToolResult(ToolCallStatus.Canceled, DeniedResult,
                    JsonSerializer.SerializeToElement(DeniedResult), DeniedResult);
            }
        }

        if (_request.ToolExecutionCheckpoint is not null)
        {
            await _request.ToolExecutionCheckpoint.BeforeExecutionAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
    }
}
