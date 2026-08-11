using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

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

    private readonly DesktopAgentDefinitionService _agentDefinitionService;
    private readonly SubagentDefinitionCatalog _subagentCatalog;
    private readonly IExtensionSettingsService _settingsService;
    private readonly IExtensionStateChangeNotifier _stateChangeNotifier;

    public AgentSettingsBridge(
        DesktopAgentDefinitionService agentDefinitionService,
        SubagentDefinitionCatalog subagentCatalog,
        IExtensionSettingsService settingsService,
        IExtensionStateChangeNotifier stateChangeNotifier)
    {
        _agentDefinitionService = agentDefinitionService;
        _subagentCatalog = subagentCatalog;
        _settingsService = settingsService;
        _stateChangeNotifier = stateChangeNotifier;
    }

    /// <summary>
    /// Agent/Subagent 定义落盘后触发（先于 revision 推进），宿主据此刷新运行时 Agent 缓存。
    /// </summary>
    public event Action? AgentsChanged;

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
                    var state = await GetStateAsync(cancellationToken);
                    response = new { type, requestId, state };
                    break;
                }
                case "agents/save-agent":
                {
                    var agent = SaveAgent(payload);
                    var revision = NotifyMutation();
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/set-binding":
                {
                    var agent = await SetExtensionBindingAsync(payload, cancellationToken);
                    var revision = NotifyMutation();
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/set-subagent-binding":
                {
                    var agent = SetSubagentBinding(payload);
                    var revision = NotifyMutation();
                    response = new { type, requestId, ok = true, revision, agent };
                    break;
                }
                case "agents/save-subagent":
                {
                    var subagent = SaveSubagent(payload);
                    var revision = NotifyMutation();
                    response = new { type, requestId, ok = true, revision, subagent };
                    break;
                }
                case "agents/set-subagent-extension-binding":
                {
                    var subagent = await SetSubagentExtensionBindingAsync(payload, cancellationToken);
                    var revision = NotifyMutation();
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

    private async Task<AgentSettingsState> GetStateAsync(CancellationToken cancellationToken)
    {
        var extensionState = await _settingsService.GetStateAsync(cancellationToken);
        var revision = _stateChangeNotifier.AdvanceTo(extensionState.Revision);
        return new AgentSettingsState(
            revision,
            _agentDefinitionService.LoadAll().Select(CreateAgentView).ToArray(),
            _subagentCatalog.LoadAll().Select(CreateSubagentView).ToArray(),
            extensionState.Plugins,
            extensionState.Skills,
            extensionState.McpServers);
    }

    private AgentDefinitionView SaveAgent(JsonElement payload)
    {
        var agent = FindAgent(ReadRequiredString(payload, "id"));
        var saved = _agentDefinitionService.Save(agent with
        {
            Name = ReadRequiredString(payload, "name"),
            Description = ReadOptionalString(payload, "description") ?? string.Empty,
            Mode = ParseMode(ReadRequiredString(payload, "mode")),
            Instructions = ReadOptionalString(payload, "instructions") ?? string.Empty
        });
        return CreateAgentView(saved);
    }

    private async Task<AgentDefinitionView> SetExtensionBindingAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var key = ReadItemKey(payload);
        var extensionState = await _settingsService.GetStateAsync(cancellationToken);
        EnsureItemExists(extensionState, key);
        var saved = _agentDefinitionService.SetExtensionBinding(
            ReadRequiredString(payload, "agentId"),
            key,
            ReadRequiredBoolean(payload, "enabled"));
        return CreateAgentView(saved);
    }

    private AgentDefinitionView SetSubagentBinding(JsonElement payload)
    {
        var agent = FindAgent(ReadRequiredString(payload, "agentId"));
        var subagentId = ReadRequiredString(payload, "subagentId");
        var subagent = _subagentCatalog.Get(subagentId)
            ?? throw new KeyNotFoundException($"Subagent '{subagentId}' was not found.");
        var saved = _agentDefinitionService.Save(agent with
        {
            SubagentIds = SetListItem(
                agent.SubagentIds,
                subagent.Id,
                ReadRequiredBoolean(payload, "enabled"))
        });
        return CreateAgentView(saved);
    }

    private SubagentDefinitionView SaveSubagent(JsonElement payload)
    {
        var id = ReadRequiredString(payload, "id");
        var existing = _subagentCatalog.Get(id)
            ?? throw new KeyNotFoundException($"Subagent '{id}' was not found.");
        var saved = _subagentCatalog.Save(existing with
        {
            Name = ReadRequiredString(payload, "name"),
            Description = ReadRequiredString(payload, "description"),
            ModelProfileId = ReadOptionalGuid(payload, "modelProfileId"),
            ToolPolicy = ReadRequiredString(payload, "toolPolicy"),
            MaxRunSeconds = ReadRequiredInt32(payload, "maxRunSeconds"),
            Instructions = ReadRequiredString(payload, "instructions")
        });
        return CreateSubagentView(saved);
    }

    private async Task<SubagentDefinitionView> SetSubagentExtensionBindingAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var subagentId = ReadRequiredString(payload, "subagentId");
        var existing = _subagentCatalog.Get(subagentId)
            ?? throw new KeyNotFoundException($"Subagent '{subagentId}' was not found.");
        var key = ReadItemKey(payload);
        var extensionState = await _settingsService.GetStateAsync(cancellationToken);
        EnsureItemExists(extensionState, key);
        var enabled = ReadRequiredBoolean(payload, "enabled");
        var updated = key.Kind switch
        {
            ExtensionKind.Plugin => existing with
            {
                PluginIds = SetListItem(existing.PluginIds, key.Id, enabled)
            },
            ExtensionKind.Skill => existing with
            {
                SkillIds = SetListItem(existing.SkillIds, key.Id, enabled)
            },
            ExtensionKind.McpServer => existing with
            {
                McpServerIds = SetListItem(existing.McpServerIds, key.Id, enabled)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unsupported extension kind.")
        };
        return CreateSubagentView(_subagentCatalog.Save(updated));
    }

    private long NotifyMutation()
    {
        AgentsChanged?.Invoke();
        return _stateChangeNotifier.Advance();
    }

    private DesktopAgentDefinition FindAgent(string agentId)
        => _agentDefinitionService.LoadAll().FirstOrDefault(item =>
               string.Equals(item.Id, agentId, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Agent '{agentId}' was not found.");

    private static AgentDefinitionView CreateAgentView(DesktopAgentDefinition agent)
        => new(
            agent.Id,
            agent.Name,
            agent.Description,
            agent.Mode == AgentExecutionMode.Cli ? "cli" : "direct",
            agent.PluginIds,
            agent.SkillIds,
            agent.McpServerIds,
            agent.SubagentIds,
            agent.Instructions,
            agent.IsBuiltIn,
            agent.Warnings);

    private static SubagentDefinitionView CreateSubagentView(SubagentDefinition subagent)
        => new(
            subagent.Id,
            subagent.Name,
            subagent.Description,
            subagent.ModelProfileId,
            subagent.ToolPolicy,
            subagent.PluginIds,
            subagent.SkillIds,
            subagent.McpServerIds,
            subagent.MaxRunSeconds,
            subagent.Instructions,
            subagent.IsValid,
            subagent.Diagnostics);

    private static void EnsureItemExists(ExtensionSettingsState state, ExtensionItemKey key)
    {
        var exists = key.Kind switch
        {
            ExtensionKind.Plugin => state.Plugins.Any(item => IdEquals(item.Id, key.Id)),
            ExtensionKind.Skill => state.Skills.Any(item => IdEquals(item.Id, key.Id)),
            ExtensionKind.McpServer => state.McpServers.Any(item => IdEquals(item.Id, key.Id)),
            _ => false
        };
        if (!exists)
        {
            throw new KeyNotFoundException($"{key.Kind} extension '{key.Id}' was not found.");
        }
    }

    private static AgentExecutionMode ParseMode(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "direct" => AgentExecutionMode.Direct,
            "cli" => AgentExecutionMode.Cli,
            _ => throw new ArgumentException($"Agent mode '{value}' is invalid.")
        };

    private static IReadOnlyList<string> SetListItem(IReadOnlyList<string> values, string id, bool enabled)
    {
        var results = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled)
        {
            results.Add(id.Trim());
        }
        else
        {
            results.Remove(id.Trim());
        }

        return results.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

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

    private static bool IdEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
