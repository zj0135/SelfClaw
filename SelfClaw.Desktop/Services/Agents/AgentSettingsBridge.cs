using SelfClaw.Desktop.Services.Agents.Models;
using SelfClaw.Desktop.Services.Agents.Definitions;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Agents;

/// <summary>
/// 代理助手设置页的 WebView 桥接：暴露 Agent/Subagent 定义查询与基本信息、扩展绑定、
/// Subagent 白名单的维护能力。所有变更直接落盘到 agents/subagents 目录的 .md 定义文件。
/// </summary>
internal sealed class AgentSettingsBridge
{
    private const string MessagePrefix = "agents/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AgentSettingsService _service;

    public AgentSettingsBridge(AgentSettingsService service) => _service = service;

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!type.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            object response;
            switch (type)
            {
                case "agents/get-state":
                {
                    var state = await _service.GetStateAsync(cancellationToken);
                    response = new { type, requestId, state };
                    break;
                }
                // 编辑器（Composer）顶栏的代理切换入口：只回传轻量展示字段（id/name/mode/description/isBuiltIn），
                // 不拉取扩展状态；当前选中态由 transcript 推送的 selectedAgentId 提供，所以这里不带。
                case "agents/list-composer-agents":
                {
                    var agents = _service.ListAgents()
                        .Select(item => new
                        {
                            id = item.Id,
                            name = item.Name,
                            mode = item.Mode == AgentExecutionMode.Cli ? "cli" : "direct",
                            description = item.Description,
                            isBuiltIn = item.IsBuiltIn,
                            warnings = item.Warnings
                        })
                        .ToArray();
                    response = new { type, requestId, agents };
                    break;
                }
                case "agents/create-agent":
                {
                    var agent = _service.CreateAgent(ReadAgentEdit(payload));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/save-agent":
                {
                    var agent = _service.SaveAgent(ReadAgentEdit(payload));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/delete-agent":
                {
                    _service.DeleteAgent(ReadRequiredString(payload, "id"));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision };
                    break;
                }
                case "agents/set-binding":
                {
                    var agent = await _service.SetExtensionBindingAsync(ReadRequiredString(payload, "agentId"), ReadItemKey(payload), ReadRequiredBoolean(payload, "enabled"), cancellationToken);
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/set-subagent-binding":
                {
                    var agent = _service.SetSubagentBinding(ReadRequiredString(payload, "agentId"), ReadRequiredString(payload, "subagentId"), ReadRequiredBoolean(payload, "enabled"));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/create-subagent":
                {
                    var subagent = _service.CreateSubagent(ReadSubagentEdit(payload, creating: true));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, subagent };
                    break;
                }
                case "agents/save-subagent":
                {
                    var subagent = _service.SaveSubagent(ReadSubagentEdit(payload, creating: false));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, subagent };
                    break;
                }
                case "agents/delete-subagent":
                {
                    _service.DeleteSubagent(ReadRequiredString(payload, "id"));
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision };
                    break;
                }
                case "agents/set-subagent-extension-binding":
                {
                    var subagent = await _service.SetSubagentExtensionBindingAsync(ReadRequiredString(payload, "subagentId"), ReadItemKey(payload), ReadRequiredBoolean(payload, "enabled"), cancellationToken);
                    var revision = _service.Revision;
                    response = new { type, requestId, ok = true, revision, subagent };
                    break;
                }
                default:
                    response = new { type, requestId, error = $"Unsupported agent message type '{type}'." };
                    break;
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new { type, requestId, error = exception.Message };
        }
    }

    private static AgentEdit ReadAgentEdit(JsonElement payload) => new(
        ReadRequiredString(payload, "id"), ReadRequiredString(payload, "name"),
        ReadOptionalString(payload, "description") ?? string.Empty,
        (ReadOptionalString(payload, "mode") ?? "direct").Trim().ToLowerInvariant() switch
        {
            "cli" => AgentExecutionMode.Cli,
            "direct" => AgentExecutionMode.Direct,
            _ => throw new ArgumentException("Agent mode is invalid.")
        }, ReadOptionalString(payload, "instructions") ?? string.Empty);

    private static SubagentEdit ReadSubagentEdit(JsonElement payload, bool creating) => new(
        ReadRequiredString(payload, "id"), ReadRequiredString(payload, "name"), ReadRequiredString(payload, "description"),
        ReadOptionalGuid(payload, "modelProfileId"),
        creating ? SubagentDefinitionCatalog.DefaultToolPolicy : ReadRequiredString(payload, "toolPolicy"),
        creating ? SubagentDefinitionCatalog.DefaultMaxRunSeconds : ReadRequiredInt32(payload, "maxRunSeconds"),
        ReadOptionalString(payload, "instructions") ?? string.Empty);

    private static ExtensionItemKey ReadItemKey(JsonElement payload)
        => new(
            ReadRequiredEnum<ExtensionKind>(payload, "kind"),
            ReadRequiredString(payload, "id"));

    private static TEnum ReadRequiredEnum<TEnum>(JsonElement payload, string propertyName)
        where TEnum : struct, Enum
    {
        if (!payload.TryGetProperty(propertyName, out var element))
        {
            throw new ArgumentException($"Property '{propertyName}' is required.");
        }

        var result = element.Deserialize<TEnum>(JsonOptions);
        return Enum.IsDefined(result)
            ? result
            : throw new ArgumentException($"Property '{propertyName}' has an unsupported value.");
    }

    private static bool ReadRequiredBoolean(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Boolean property '{propertyName}' is required.");
        }

        return element.GetBoolean();
    }

    private static int ReadRequiredInt32(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) ||
            !element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"Integer property '{propertyName}' is required.");
        }

        return value;
    }

    private static Guid? ReadOptionalGuid(JsonElement payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        if (value is null)
        {
            return null;
        }

        return Guid.TryParse(value, out var guid) && guid != Guid.Empty
            ? guid
            : throw new ArgumentException($"Property '{propertyName}' must be a valid GUID.");
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName)
        => ReadOptionalString(payload, propertyName)
            ?? throw new ArgumentException($"String property '{propertyName}' is required.");

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

}
