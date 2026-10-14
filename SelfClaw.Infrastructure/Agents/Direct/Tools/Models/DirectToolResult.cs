using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record DirectToolResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ToolCallStatus>))] ToolCallStatus Status,
    string Summary,
    JsonElement Content,
    [property: JsonIgnore] string? Detail);
