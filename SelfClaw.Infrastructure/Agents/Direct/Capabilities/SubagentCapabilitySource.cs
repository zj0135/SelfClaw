using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// Advertises the parent Agent's allowlisted Subagents and exposes the four delegation tools. The
/// allowlist - never the definition file - decides what is callable, so a bound definition that was
/// deleted or is invalid still reaches the coordinator and records its durable failure envelope, while an
/// id outside the allowlist is answered as a recoverable tool result instead of an exception.
/// </summary>
internal sealed class SubagentCapabilitySource
{
    internal const string DelegateToolName = "delegate_to_subagent";
    internal const string GetTaskToolName = "get_subagent_task";
    internal const string CancelTaskToolName = "cancel_subagent_task";
    internal const string RetryTaskToolName = "retry_subagent_task";

    private const string DelegateDisplayName = "Delegate to Subagent";
    private const string GetTaskDisplayName = "Get Subagent task";
    private const string CancelTaskDisplayName = "Cancel Subagent task";
    private const string RetryTaskDisplayName = "Retry Subagent task";

    private const string MissingDefinition = "no usable definition file, so it cannot start";
    private const string ParameterName = "subagentId";

    private readonly ISubagentTaskCoordinator? _coordinator;
    private readonly ISubagentDefinitionCatalog? _definitionCatalog;

    public SubagentCapabilitySource(
        ISubagentTaskCoordinator? coordinator,
        ISubagentDefinitionCatalog? definitionCatalog = null)
    {
        _coordinator = coordinator;
        _definitionCatalog = definitionCatalog;
    }

    internal SubagentCapabilities Resolve(
        DirectChatTurnRequest request,
        DirectCapabilityCeiling capabilityCeiling)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilityCeiling);
        var coordinator = _coordinator;
        if (coordinator is null ||
            request.ExecutionContext.Origin == DirectTurnOrigin.Subagent ||
            request.Agent.SubagentIds.Count == 0)
        {
            return new SubagentCapabilities([], []);
        }

        if (request.ModelProfileId is not Guid modelProfileId)
        {
            throw new InvalidOperationException("Subagent delegation requires a concrete parent model profile.");
        }

        var subagentIds = request.Agent.SubagentIds;
        var bound = new BoundTools(coordinator, request, capabilityCeiling, modelProfileId, subagentIds);
        return new SubagentCapabilities(
            [CapabilitySections.SubagentCatalog(ResolveCatalog(subagentIds), DelegateToolName)],
            [
                CreateDelegateTool(bound, subagentIds),
                Create(bound.GetAsync, GetTaskToolName,
                    "Get the current state of a Subagent task created by this conversation.",
                    ToolCallKind.Read, GetTaskDisplayName),
                Create(bound.CancelAsync, CancelTaskToolName,
                    "Cancel a queued or running Subagent task created by this conversation.",
                    ToolCallKind.Run, CancelTaskDisplayName),
                Create(bound.RetryAsync, RetryTaskToolName,
                    "Retry a terminal Subagent task as a new durable attempt.",
                    ToolCallKind.Run, RetryTaskDisplayName)
            ]);
    }

    /// <summary>
    /// Enriches the allowlist with the names and descriptions the model needs to choose correctly. A bound
    /// id without a usable definition keeps its slot so the catalog stays an exact mirror of what the
    /// coordinator will accept.
    /// </summary>
    private IReadOnlyList<SubagentCatalogEntry> ResolveCatalog(IReadOnlyList<string> subagentIds)
    {
        if (_definitionCatalog is null)
        {
            return subagentIds.Select(id => new SubagentCatalogEntry(id, id, string.Empty, null)).ToArray();
        }

        var known = _definitionCatalog.GetEntries(subagentIds)
            .ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        return subagentIds
            .Select(id => known.TryGetValue(id, out var entry)
                ? entry
                : new SubagentCatalogEntry(id, id, MissingDefinition, null))
            .ToArray();
    }

    private static DirectToolBinding CreateDelegateTool(BoundTools bound, IReadOnlyList<string> subagentIds)
    {
        var tool = AIFunctionFactory.Create(bound.DelegateAsync, new AIFunctionFactoryOptions
        {
            Name = DelegateToolName,
            Description = CreateDelegateDescription(subagentIds),
            ExcludeResultSchema = true,
            MarshalResult = MarshalDirectResult(DelegateToolName)
        });
        return new DirectToolBinding(
            new EnumeratedSubagentFunction(tool, EnumerateSubagentIds(tool.JsonSchema, subagentIds)),
            new DirectToolDescriptor(
                DelegateToolName,
                ToolCallKind.Run,
                ToolSourceKind.BuiltIn,
                DisplayName: DelegateDisplayName));
    }

    private static string CreateDelegateDescription(IReadOnlyList<string> subagentIds)
        => "Queue an authorized Subagent task and return immediately with its durable task state. " +
           $"Allowed {ParameterName} values: {string.Join(", ", subagentIds)}. " +
           "Pass one of those exact ids; never invent, translate or guess a Subagent id.";

    /// <summary>
    /// Constrains the generated schema to the allowlist. The factory cannot derive an enum from a delegate
    /// parameter, and a structural constraint holds better than a description the model may ignore.
    /// </summary>
    private static JsonElement EnumerateSubagentIds(JsonElement schema, IReadOnlyList<string> subagentIds)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root ||
            root["properties"] is not JsonObject properties ||
            properties[ParameterName] is not JsonObject parameter)
        {
            return schema;
        }

        parameter["enum"] = new JsonArray(subagentIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    private static DirectToolBinding Create(
        Delegate method,
        string name,
        string description,
        ToolCallKind kind,
        string displayName)
        => new(AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name, Description = description, ExcludeResultSchema = true,
            MarshalResult = MarshalDirectResult(name)
        }), new DirectToolDescriptor(name, kind, ToolSourceKind.BuiltIn, DisplayName: displayName));

    private static Func<object?, Type?, CancellationToken, ValueTask<object?>> MarshalDirectResult(string toolName)
        => (value, _, _) => value is DirectToolResult result
            ? ValueTask.FromResult<object?>(result)
            : throw new InvalidOperationException($"Tool '{toolName}' returned an invalid result.");

    /// <summary>
    /// Replaces the generated schema with the allowlist-constrained one. <see cref="DelegatingAIFunction"/>
    /// delegates everything else - including invocation - to the factory-created function.
    /// </summary>
    private sealed class EnumeratedSubagentFunction(AIFunction inner, JsonElement jsonSchema) : DelegatingAIFunction(inner)
    {
        public override JsonElement JsonSchema => jsonSchema;
    }

    private sealed class BoundTools
    {
        private readonly ISubagentTaskCoordinator _coordinator;
        private readonly DirectChatTurnRequest _request;
        private readonly DirectCapabilityCeiling _capabilityCeiling;
        private readonly Guid _modelProfileId;
        private readonly IReadOnlyList<string> _subagentIds;

        public BoundTools(
            ISubagentTaskCoordinator coordinator,
            DirectChatTurnRequest request,
            DirectCapabilityCeiling capabilityCeiling,
            Guid modelProfileId,
            IReadOnlyList<string> subagentIds)
        {
            _coordinator = coordinator;
            _request = request;
            _capabilityCeiling = capabilityCeiling;
            _modelProfileId = modelProfileId;
            _subagentIds = subagentIds;
        }

        public Task<DirectToolResult> DelegateAsync(
            [Description("Exact allowlisted Subagent id.")] string subagentId,
            [Description("Complete, explicit task for the isolated Subagent. Parent history is not copied.")] string task,
            CancellationToken cancellationToken)
        {
            var requestedId = subagentId?.Trim() ?? string.Empty;
            if (!_subagentIds.Contains(requestedId, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(Failed(
                    $"Subagent '{requestedId}' is not authorized for this Agent. " +
                    $"Available {ParameterName} values: {string.Join(", ", _subagentIds)}."));
            }

            return StartAsync(requestedId, task, cancellationToken);
        }

        public async Task<DirectToolResult> GetAsync(
            [Description("Durable Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
        {
            var view = await _coordinator.GetAsync(
                    new SubagentTaskQuery(_request.ConversationId, taskId),
                    cancellationToken)
                .ConfigureAwait(false);
            return Completed(GetTaskDisplayName, view);
        }

        public async Task<DirectToolResult> CancelAsync(
            [Description("Durable Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
        {
            try
            {
                var view = await _coordinator.CancelAsync(
                        new SubagentTaskCommand(_request.ConversationId, taskId),
                        cancellationToken)
                    .ConfigureAwait(false);
                return Completed(CancelTaskDisplayName, view);
            }
            catch (KeyNotFoundException)
            {
                return Failed(MissingTask(taskId));
            }
        }

        public async Task<DirectToolResult> RetryAsync(
            [Description("Terminal Subagent task id.")] Guid taskId,
            CancellationToken cancellationToken)
        {
            try
            {
                var view = await _coordinator.RetryAsync(
                        new SubagentTaskRetryRequest(_request.ConversationId, _request.TurnId, taskId),
                        cancellationToken)
                    .ConfigureAwait(false);
                return Completed(RetryTaskDisplayName, view);
            }
            catch (KeyNotFoundException)
            {
                return Failed(MissingTask(taskId));
            }
        }

        private async Task<DirectToolResult> StartAsync(
            string subagentId,
            string task,
            CancellationToken cancellationToken)
        {
            var view = await _coordinator.StartAsync(
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
                    cancellationToken)
                .ConfigureAwait(false);
            return Completed(DelegateDisplayName, view);
        }

        private static string MissingTask(Guid taskId)
            => $"No Subagent task '{taskId}' exists in this conversation.";

        private static DirectToolResult Completed(string displayName, object? payload)
        {
            var content = JsonSerializer.SerializeToElement(payload);
            return new DirectToolResult(ToolCallStatus.Completed, displayName, content, content.ToString());
        }

        private static DirectToolResult Failed(string message)
            => new(ToolCallStatus.Failed, message, JsonSerializer.SerializeToElement(message), message);
    }
}
