using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Extensions.Abstractions;

namespace SelfClaw.Desktop.Services.Extensions;

internal sealed class ExtensionSettingsBridge
{
    private const string MessagePrefix = "extensions/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IExtensionSettingsService _settingsService;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly DesktopAgentDefinitionService _agentDefinitionService;
    private readonly IExtensionPackagePicker _packagePicker;
    private readonly IExtensionStateChangeNotifier _stateChangeNotifier;

    public ExtensionSettingsBridge(
        IExtensionSettingsService settingsService,
        IExtensionPackageRepository packageRepository,
        DesktopAgentDefinitionService agentDefinitionService,
        IExtensionPackagePicker packagePicker,
        IExtensionStateChangeNotifier stateChangeNotifier)
    {
        _settingsService = settingsService;
        _packageRepository = packageRepository;
        _agentDefinitionService = agentDefinitionService;
        _packagePicker = packagePicker;
        _stateChangeNotifier = stateChangeNotifier;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        string? activeAgentId = null,
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
                case "extensions/import-package":
                {
                    var kind = ReadRequiredEnum<ExtensionKind>(payload, "kind");
                    var selectedPath = _packagePicker.PickPackage(kind);
                    if (selectedPath is null)
                    {
                        response = new { type, requestId, ok = false, cancelled = true };
                        break;
                    }

                    var package = await _settingsService.ImportPackageAsync(kind, selectedPath, cancellationToken);
                    var record = await _packageRepository.GetPackageAsync(kind, package.Id, cancellationToken)
                        ?? throw new InvalidOperationException("Imported package was not persisted.");
                    using var manifestDocument = JsonDocument.Parse(record.ManifestJson);
                    var revision = await AdvanceMutationRevisionAsync(cancellationToken);
                    response = new
                    {
                        type,
                        requestId,
                        ok = true,
                        package,
                        revision,
                        summary = new
                        {
                            manifest = manifestDocument.RootElement.Clone(),
                            contentHash = record.ContentHash,
                            fileCount = CountPackageFiles(record.InstallPath)
                        }
                    };
                    break;
                }
                case "extensions/get-state":
                {
                    var state = await GetStateAsync(activeAgentId, cancellationToken);
                    response = new { type, requestId, state };
                    break;
                }
                case "extensions/list-effective-skills":
                {
                    var (agent, skills, revision) = await ListEffectiveSkillsAsync(
                        ReadOptionalString(payload, "agentId"),
                        activeAgentId,
                        cancellationToken);
                    response = new { type, requestId, agentId = agent.Id, skills, revision };
                    break;
                }
                case "extensions/set-enabled":
                    await _settingsService.SetEnabledAsync(
                        ReadItemKey(payload),
                        ReadRequiredBoolean(payload, "enabled"),
                        cancellationToken);
                    response = await BuildMutationResponseAsync(type, requestId, cancellationToken);
                    break;
                case "extensions/acknowledge-plugin-permissions":
                    await _settingsService.AcknowledgePluginPermissionsAsync(
                        ReadRequiredString(payload, "id"),
                        ReadStringArray(payload, "permissions"),
                        cancellationToken);
                    response = await BuildMutationResponseAsync(type, requestId, cancellationToken);
                    break;
                case "extensions/delete":
                    await _settingsService.DeleteAsync(ReadItemKey(payload), cancellationToken);
                    response = await BuildMutationResponseAsync(type, requestId, cancellationToken);
                    break;
                case "extensions/save-mcp":
                {
                    var server = await _settingsService.SaveMcpServerAsync(
                        ReadSaveMcpServerCommand(payload),
                        cancellationToken);
                    var revision = await AdvanceMutationRevisionAsync(cancellationToken);
                    response = new { type, requestId, server, revision };
                    break;
                }
                case "extensions/test-mcp":
                {
                    var result = await _settingsService.TestMcpServerAsync(
                        ReadRequiredString(payload, "id"),
                        cancellationToken);
                    var revision = await AdvanceMutationRevisionAsync(cancellationToken);
                    response = new { type, requestId, result, revision };
                    break;
                }
                case "extensions/set-agent-binding":
                {
                    var key = ReadItemKey(payload);
                    var serviceState = await _settingsService.GetStateAsync(cancellationToken);
                    EnsureItemExists(serviceState, key);
                    var agent = _agentDefinitionService.SetExtensionBinding(
                        ReadRequiredString(payload, "agentId"),
                        key,
                        ReadRequiredBoolean(payload, "enabled"));
                    var revision = _stateChangeNotifier.Advance();
                    response = new
                    {
                        type,
                        requestId,
                        ok = true,
                        revision,
                        agent = CreateAgentView(agent)
                    };
                    break;
                }
                default:
                    response = new { type, requestId, error = $"Unsupported extension message type '{type}'." };
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

    private async Task<ExtensionSettingsState> GetStateAsync(
        string? activeAgentId,
        CancellationToken cancellationToken)
    {
        var state = await _settingsService.GetStateAsync(cancellationToken);
        var agents = _agentDefinitionService.LoadAll().Select(CreateAgentView).ToArray();
        var revision = _stateChangeNotifier.AdvanceTo(state.Revision);
        return state with
        {
            Revision = revision,
            ActiveAgentId = ResolveActiveAgentId(agents, activeAgentId),
            Agents = agents,
            Plugins = AddAssignments(state.Plugins, agents, agent => agent.PluginIds),
            Skills = AddManagedAssignments(state.Skills, agents, agent => agent.SkillIds),
            McpServers = AddMcpAssignments(state.McpServers, agents)
        };
    }

    private async Task<(ExtensionAgentView Agent, IReadOnlyList<ExtensionPackageView> Skills, long Revision)>
        ListEffectiveSkillsAsync(
            string? requestedAgentId,
            string? activeAgentId,
            CancellationToken cancellationToken)
    {
        var agents = _agentDefinitionService.LoadAll().Select(CreateAgentView).ToArray();
        var agentId = requestedAgentId ?? ResolveActiveAgentId(agents, activeAgentId)
            ?? throw new InvalidOperationException("No active Agent is available.");
        var agent = agents.FirstOrDefault(candidate => IdEquals(candidate.Id, agentId))
            ?? throw new KeyNotFoundException($"Agent '{agentId}' was not found.");
        var state = await _settingsService.GetStateAsync(cancellationToken);
        var skills = state.Skills
            .Where(skill => skill.Enabled &&
                            skill.Status == ExtensionStatus.Ready &&
                            (string.IsNullOrWhiteSpace(skill.SourcePluginId)
                                ? agent.SkillIds.Contains(skill.Id, StringComparer.OrdinalIgnoreCase)
                                : agent.PluginIds.Contains(skill.SourcePluginId, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (agent, skills, _stateChangeNotifier.AdvanceTo(state.Revision));
    }

    private async Task<object> BuildMutationResponseAsync(
        string type,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var revision = await AdvanceMutationRevisionAsync(cancellationToken);
        return new { type, requestId, ok = true, revision };
    }

    private async Task<long> AdvanceMutationRevisionAsync(CancellationToken cancellationToken)
    {
        var state = await _settingsService.GetStateAsync(cancellationToken);
        return _stateChangeNotifier.AdvanceTo(state.Revision);
    }

    private static string? ResolveActiveAgentId(
        IReadOnlyList<ExtensionAgentView> agents,
        string? activeAgentId)
        => agents.Any(agent => string.Equals(agent.Id, activeAgentId, StringComparison.OrdinalIgnoreCase))
            ? activeAgentId
            : agents.FirstOrDefault(agent => string.Equals(
                agent.Id,
                DesktopAgentDefinitionService.BuildAgentId,
                StringComparison.OrdinalIgnoreCase))?.Id;

    private static IReadOnlyList<ExtensionPackageView> AddAssignments(
        IReadOnlyList<ExtensionPackageView> packages,
        IReadOnlyList<ExtensionAgentView> agents,
        Func<ExtensionAgentView, IReadOnlyList<string>> selectIds)
        => packages.Select(package => package with
        {
            AssignedAgentIds = agents
                .Where(agent => selectIds(agent).Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                .Select(agent => agent.Id)
                .ToArray()
        }).ToArray();

    private static IReadOnlyList<ExtensionPackageView> AddManagedAssignments(
        IReadOnlyList<ExtensionPackageView> packages,
        IReadOnlyList<ExtensionAgentView> agents,
        Func<ExtensionAgentView, IReadOnlyList<string>> selectIds)
        => packages.Select(package => package with
        {
            AssignedAgentIds = agents
                .Where(agent => string.IsNullOrWhiteSpace(package.SourcePluginId)
                    ? selectIds(agent).Contains(package.Id, StringComparer.OrdinalIgnoreCase)
                    : agent.PluginIds.Contains(package.SourcePluginId, StringComparer.OrdinalIgnoreCase))
                .Select(agent => agent.Id)
                .ToArray()
        }).ToArray();

    private static IReadOnlyList<McpServerView> AddMcpAssignments(
        IReadOnlyList<McpServerView> servers,
        IReadOnlyList<ExtensionAgentView> agents)
        => servers.Select(server => server with
        {
            AssignedAgentIds = agents
                .Where(agent => string.IsNullOrWhiteSpace(server.SourcePluginId)
                    ? agent.McpServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)
                    : agent.PluginIds.Contains(server.SourcePluginId, StringComparer.OrdinalIgnoreCase))
                .Select(agent => agent.Id)
                .ToArray()
        }).ToArray();

    private static ExtensionAgentView CreateAgentView(DesktopAgentDefinition agent)
        => new(agent.Id, agent.Name, agent.PluginIds, agent.SkillIds, agent.McpServerIds);

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

    private static SaveMcpServerCommand ReadSaveMcpServerCommand(JsonElement payload)
        => new(
            ReadOptionalString(payload, "id"),
            ReadOptionalString(payload, "displayName") ?? ReadRequiredString(payload, "name"),
            ReadRequiredEnum<McpTransportKind>(payload, "transport"),
            ReadOptionalString(payload, "command"),
            ReadStringArray(payload, "arguments"),
            ReadOptionalString(payload, "workingDirectoryMode"),
            ReadOptionalBoolean(payload, "requiresWorkspace") ?? false,
            ReadEntries(payload, "environment"),
            ReadOptionalString(payload, "endpoint"),
            ReadOptionalString(payload, "transportMode"),
            ReadOptionalInt32(payload, "connectionTimeoutSeconds"),
            ReadEntries(payload, "headers"),
            ReadOptionalBoolean(payload, "enabled"));

    private static IReadOnlyList<McpKeyValueCommand> ReadEntries(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Property '{propertyName}' must be an array.");
        }

        return element.Deserialize<McpKeyValueCommand[]>(JsonOptions) ?? [];
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element))
        {
            throw new ArgumentException($"Array property '{propertyName}' is required.");
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Property '{propertyName}' must be an array.");
        }

        return element.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new ArgumentException($"Property '{propertyName}' must contain only strings.")).ToArray();
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
        => ReadOptionalBoolean(payload, propertyName)
            ?? throw new ArgumentException($"Boolean property '{propertyName}' is required.");

    private static bool? ReadOptionalBoolean(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw new ArgumentException($"Property '{propertyName}' must be a boolean.");
    }

    private static int? ReadOptionalInt32(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.TryGetInt32(out var value)
            ? value
            : throw new ArgumentException($"Property '{propertyName}' must be an integer.");
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

    private static int CountPackageFiles(string installPath)
        => Directory.Exists(installPath)
            ? Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories).Count()
            : 0;
}
