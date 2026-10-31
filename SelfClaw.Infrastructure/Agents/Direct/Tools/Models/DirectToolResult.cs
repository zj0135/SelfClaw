using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record DirectToolResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ToolCallStatus>))] ToolCallStatus Status,
    string Summary,
    JsonElement Content,
    [property: JsonIgnore] string? Detail,
    /// <summary>
    /// Model-visible payload for the delivery paths that carry non-text parts. The shape is fixed:
    /// element 0 is the text header and every following element is a <see cref="DataContent"/> image in
    /// delivery order. It is in-memory only — it never reaches storage, logs, summaries or replay.
    /// </summary>
    [property: JsonIgnore] IReadOnlyList<AIContent>? ModelContents = null);
