using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using System.Net;
using System.Text.Json;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Processes;

namespace SelfClaw.Infrastructure.Extensions.Plugins;

internal sealed class PluginManifestReader
{
    private const int MaximumPanelTitleLength = 40;
    private const int MinimumPanelWidth = 280;
    private const int MaximumPanelWidth = 720;
    private const int FallbackPanelWidth = 360;
    private const int MaximumHooksPerPlugin = 32;
    private const int MaximumHookPatternLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ExtensionPackageLimits _limits;

    public PluginManifestReader(ExtensionPackageLimits limits)
    {
        _limits = limits;
    }

    public async Task<PluginManifest> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException("Plugin package does not contain plugin.json.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _limits.MaximumManifestBytes)
        {
            throw new InvalidDataException("plugin.json exceeds the manifest size limit.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        RawPluginManifest? raw;
        try
        {
            raw = await JsonSerializer.DeserializeAsync<RawPluginManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("plugin.json is not valid JSON.", exception);
        }

        if (raw is null || raw.SchemaVersion != 1)
        {
            throw new InvalidDataException("Plugin schemaVersion must be 1.");
        }

        ValidateId(raw.Id, "Plugin id");
        ArgumentException.ThrowIfNullOrWhiteSpace(raw.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(raw.Version);
        var packageRoot = Path.GetDirectoryName(fullPath)!;
        var contributions = raw.Contributes ?? new RawPluginContributions();
        var directInstructions = ValidateOptionalFile(
            packageRoot,
            contributions.DirectInstructions,
            "directInstructions");
        var skills = ValidateSkills(packageRoot, contributions.Skills ?? []);
        var mcpServers = ValidateMcpServers(packageRoot, contributions.McpServers ?? []);
        var panels = ValidatePanels(raw.Id, packageRoot, contributions.Panels ?? []);
        var permissions = PluginPermissions.Validate(raw.Permissions);
        if (panels.Count > 0 && !PluginPermissions.Grants(permissions, PluginPermissions.Panel))
        {
            throw new InvalidDataException(
                $"Plugin declares panels, so it must also declare the '{PluginPermissions.Panel}' permission.");
        }

        var hooks = ValidateHooks(packageRoot, contributions.Hooks ?? [], permissions);

        return new PluginManifest(
            1,
            raw.Id,
            raw.Name.Trim(),
            raw.Version.Trim(),
            raw.Description?.Trim() ?? string.Empty,
            raw.Publisher?.Trim(),
            permissions,
            new PluginContributions(directInstructions, skills, mcpServers, panels, hooks));
    }

    private static IReadOnlyList<PluginPanelContribution> ValidatePanels(
        string pluginId,
        string packageRoot,
        IReadOnlyList<RawPluginPanelContribution> panels)
    {
        if (panels.Count == 0)
        {
            return [];
        }

        // Caught here rather than at open time: the panel origin is derived from the Plugin id, so an id
        // that is a legal package id but not a legal DNS label would otherwise install cleanly and then
        // fail to resolve the first time a user opens the tab.
        if (!PluginPanelOrigin.IsValidPluginLabel(pluginId))
        {
            throw new InvalidDataException(
                $"Plugin id '{pluginId}' cannot host panels: it must be at most 63 characters and must not start or end with '-'.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PluginPanelContribution>();
        foreach (var panel in panels)
        {
            ValidateId(panel.Id, "Plugin panel id");
            if (!ids.Add(panel.Id!))
            {
                throw new InvalidDataException($"Duplicate Plugin panel id '{panel.Id}'.");
            }

            var title = panel.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) ||
                title.Length > MaximumPanelTitleLength ||
                title.Any(char.IsControl))
            {
                throw new InvalidDataException($"Plugin panel '{panel.Id}' title is invalid.");
            }

            if (string.IsNullOrWhiteSpace(panel.Entry))
            {
                throw new InvalidDataException($"Plugin panel '{panel.Id}' must declare an entry.");
            }

            var entryPath = PluginCommandTemplate.ResolvePackagePath(packageRoot, panel.Entry, "Panel entry");
            if (!File.Exists(entryPath))
            {
                throw new InvalidDataException($"Plugin panel '{panel.Id}' entry file does not exist.");
            }

            if (!Path.GetExtension(entryPath).Equals(".html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Plugin panel '{panel.Id}' entry must be an .html file.");
            }

            var width = panel.DefaultWidth ?? FallbackPanelWidth;
            if (width is < MinimumPanelWidth or > MaximumPanelWidth)
            {
                throw new InvalidDataException(
                    $"Plugin panel '{panel.Id}' defaultWidth must be between {MinimumPanelWidth} and {MaximumPanelWidth}.");
            }

            results.Add(new PluginPanelContribution(
                panel.Id!,
                title,
                PluginPanelIcons.Resolve(panel.Icon),
                NormalizeRelativePath(packageRoot, entryPath),
                width));
        }

        return results;
    }

    private static IReadOnlyList<PluginSkillContribution> ValidateSkills(
        string packageRoot,
        IReadOnlyList<RawPluginSkillContribution> skills)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PluginSkillContribution>();
        foreach (var skill in skills)
        {
            ValidateId(skill.Id, "Plugin Skill id");
            if (!ids.Add(skill.Id))
            {
                throw new InvalidDataException($"Duplicate Plugin Skill id '{skill.Id}'.");
            }

            var path = PluginCommandTemplate.ResolvePackagePath(packageRoot, skill.Path, "Skill path");
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "SKILL.md")))
            {
                throw new InvalidDataException($"Plugin Skill '{skill.Id}' must contain SKILL.md.");
            }

            results.Add(new PluginSkillContribution(skill.Id, NormalizeRelativePath(packageRoot, path)));
        }

        return results;
    }

    private static IReadOnlyList<PluginMcpServerContribution> ValidateMcpServers(
        string packageRoot,
        IReadOnlyList<RawPluginMcpServerContribution> servers)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PluginMcpServerContribution>();
        foreach (var server in servers)
        {
            ValidateId(server.Id, "Plugin MCP id");
            if (!ids.Add(server.Id))
            {
                throw new InvalidDataException($"Duplicate Plugin MCP id '{server.Id}'.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
            if (server.Arguments is null)
            {
                throw new InvalidDataException($"Plugin MCP '{server.Id}' arguments must be a string array.");
            }
            var arguments = server.Arguments
                .Select(argument => argument ?? throw new InvalidDataException(
                    $"Plugin MCP '{server.Id}' arguments must contain only strings."))
                .ToArray();

            var transport = server.Transport?.Trim().ToLowerInvariant();
            if (transport == "stdio")
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(server.Command);
                PluginCommandTemplate.Validate(packageRoot, server.Command, "MCP command");
                foreach (var argument in arguments)
                {
                    PluginCommandTemplate.Validate(packageRoot, argument, "MCP argument");
                }
            }
            else if (transport == "http")
            {
                if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint) ||
                    endpoint.Scheme is not ("http" or "https"))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' endpoint is invalid.");
                }

                if (!string.IsNullOrEmpty(endpoint.UserInfo))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' endpoint must not contain credentials.");
                }

                if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' remote endpoint must use HTTPS.");
                }

                if (server.TransportMode is not (null or "auto" or "streamableHttp" or "sse"))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' transportMode is invalid.");
                }

                if (server.ConnectionTimeoutSeconds is <= 0 or > 300)
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' connection timeout is invalid.");
                }
            }
            else
            {
                throw new InvalidDataException($"Plugin MCP '{server.Id}' transport must be stdio or http.");
            }

            var requiredSettingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requiredSettings = (server.RequiredSettings ?? []).Select(setting =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(setting.Key);
                if (setting.Target is not ("env" or "header"))
                {
                    throw new InvalidDataException("Plugin MCP required setting target must be env or header.");
                }

                var key = setting.Key.Trim();
                if ((transport == "stdio" && setting.Target != "env") ||
                    (transport == "http" && setting.Target != "header"))
                {
                    throw new InvalidDataException(
                        $"Plugin MCP '{server.Id}' required settings must target " +
                        (transport == "stdio" ? "environment variables." : "HTTP headers."));
                }

                if (setting.Target == "env"
                        ? !McpSettingPath.IsValidEnvironmentKey(key)
                        : !McpSettingPath.IsValidHeaderName(key))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' required setting key '{key}' is invalid.");
                }

                if (!requiredSettingPaths.Add(McpSettingPath.ForManifestTarget(setting.Target, key)))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' required settings contain duplicates.");
                }

                return new PluginRequiredSetting(key, setting.Target, setting.Secret);
            }).ToArray();
            results.Add(new PluginMcpServerContribution(
                server.Id,
                server.Name.Trim(),
                transport,
                server.Command,
                arguments,
                server.Endpoint,
                server.TransportMode,
                server.ConnectionTimeoutSeconds,
                server.RequiresWorkspace,
                requiredSettings));
        }

        return results;
    }

    private static IReadOnlyList<PluginHookContribution> ValidateHooks(
        string packageRoot,
        IReadOnlyList<RawPluginHookContribution> hooks,
        IReadOnlyList<string> permissions)
    {
        if (hooks.Count > MaximumHooksPerPlugin)
        {
            throw new InvalidDataException($"Plugin declares more than {MaximumHooksPerPlugin} hooks.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PluginHookContribution>(hooks.Count);
        foreach (var hook in hooks)
        {
            ValidateId(hook.Id, "Plugin hook id");
            var hookId = hook.Id!;
            if (!ids.Add(hookId))
            {
                throw new InvalidDataException($"Duplicate Plugin hook id '{hookId}'.");
            }

            var hookEvent = ParseHookEvent(hookId, hook.Event);
            var permission = RequiredHookPermission(hookEvent);
            if (!PluginPermissions.Grants(permissions, permission))
            {
                throw new InvalidDataException(
                    $"Plugin hook '{hookId}' requires the '{permission}' permission.");
            }

            var runAsync = ValidateHookAsync(hookId, hookEvent, hook.Async);
            var onFailure = ValidateHookOnFailure(hookId, hookEvent, hook.OnFailure);
            var includeRequestBody = ValidateHookIncludeRequestBody(hookId, hookEvent, hook.IncludeRequestBody, permissions);
            var timeout = ValidateHookTimeout(hookId, hookEvent, runAsync, hook.TimeoutSeconds);
            var matcher = ValidateHookMatcher(hookId, hookEvent, hook.Matcher);
            var command = ValidateHookCommand(packageRoot, hookId, hook.Command);
            var arguments = ValidateHookArguments(packageRoot, hookId, hook.Arguments);

            results.Add(new PluginHookContribution(
                hookId,
                hookEvent,
                matcher,
                command,
                arguments,
                timeout,
                onFailure,
                runAsync,
                includeRequestBody));
        }

        return results;
    }

    private static PluginHookEvent ParseHookEvent(string hookId, string? value)
        => value switch
        {
            "runStarting" => PluginHookEvent.RunStarting,
            "runCompleted" => PluginHookEvent.RunCompleted,
            "toolExecuting" => PluginHookEvent.ToolExecuting,
            "toolExecuted" => PluginHookEvent.ToolExecuted,
            "httpRequestSending" => PluginHookEvent.HttpRequestSending,
            "httpResponseReceived" => PluginHookEvent.HttpResponseReceived,
            _ => throw new InvalidDataException($"Plugin hook '{hookId}' event is invalid.")
        };

    private static string RequiredHookPermission(PluginHookEvent hookEvent)
        => hookEvent switch
        {
            PluginHookEvent.RunStarting or PluginHookEvent.RunCompleted => PluginPermissions.HooksRun,
            PluginHookEvent.ToolExecuting or PluginHookEvent.ToolExecuted => PluginPermissions.HooksTool,
            _ => PluginPermissions.HooksHttp
        };

    private static bool ValidateHookAsync(string hookId, PluginHookEvent hookEvent, bool? runAsync)
    {
        if (runAsync is null)
        {
            return false;
        }

        if (hookEvent != PluginHookEvent.ToolExecuted)
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' may only declare async on toolExecuted.");
        }

        return runAsync.Value;
    }

    private static PluginHookFailurePolicy ValidateHookOnFailure(
        string hookId,
        PluginHookEvent hookEvent,
        string? value)
    {
        if (value is null)
        {
            return PluginHookFailurePolicy.Continue;
        }

        if (hookEvent is not (PluginHookEvent.RunStarting or PluginHookEvent.ToolExecuting))
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' may only declare onFailure on runStarting or toolExecuting.");
        }

        return value switch
        {
            "continue" => PluginHookFailurePolicy.Continue,
            "block" => PluginHookFailurePolicy.Block,
            _ => throw new InvalidDataException(
                $"Plugin hook '{hookId}' onFailure must be continue or block.")
        };
    }

    private static bool ValidateHookIncludeRequestBody(
        string hookId,
        PluginHookEvent hookEvent,
        bool? value,
        IReadOnlyList<string> permissions)
    {
        if (value is null)
        {
            return false;
        }

        if (hookEvent != PluginHookEvent.HttpRequestSending)
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' may only declare includeRequestBody on httpRequestSending.");
        }

        if (value.Value && !PluginPermissions.Grants(permissions, PluginPermissions.HooksHttpBody))
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' requires the '{PluginPermissions.HooksHttpBody}' permission to include the request body.");
        }

        return value.Value;
    }

    private static TimeSpan ValidateHookTimeout(
        string hookId,
        PluginHookEvent hookEvent,
        bool runAsync,
        int? seconds)
    {
        if (seconds is null)
        {
            return DefaultHookTimeout(hookEvent, runAsync);
        }

        var maximum = MaximumHookTimeoutSeconds(hookEvent, runAsync);
        if (seconds.Value < 1 || seconds.Value > maximum)
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' timeoutSeconds must be between 1 and {maximum}.");
        }

        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static TimeSpan DefaultHookTimeout(PluginHookEvent hookEvent, bool runAsync)
        => hookEvent switch
        {
            PluginHookEvent.HttpRequestSending => TimeSpan.FromSeconds(5),
            PluginHookEvent.RunCompleted or PluginHookEvent.HttpResponseReceived => TimeSpan.FromSeconds(30),
            PluginHookEvent.ToolExecuted when runAsync => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(10)
        };

    private static int MaximumHookTimeoutSeconds(PluginHookEvent hookEvent, bool runAsync)
        => hookEvent switch
        {
            PluginHookEvent.HttpRequestSending => 30,
            PluginHookEvent.RunCompleted or PluginHookEvent.HttpResponseReceived => 120,
            PluginHookEvent.ToolExecuted when runAsync => 120,
            _ => 60
        };

    private static PluginHookMatcher ValidateHookMatcher(
        string hookId,
        PluginHookEvent hookEvent,
        RawPluginHookMatcher? matcher)
    {
        if (matcher is null)
        {
            return new PluginHookMatcher([], [], [], [], [], []);
        }

        var isToolEvent = hookEvent is PluginHookEvent.ToolExecuting or PluginHookEvent.ToolExecuted;
        var isHttpEvent = hookEvent is PluginHookEvent.HttpRequestSending or PluginHookEvent.HttpResponseReceived;
        var origins = ValidateHookOrigins(hookId, matcher.Origins);

        if (!isToolEvent &&
            (matcher.Tools is not null || matcher.SourceIds is not null || matcher.Kinds is not null || matcher.Sources is not null))
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' matcher fields tools, sourceIds, kinds and sources are only valid on tool events.");
        }

        if (!isHttpEvent && matcher.Hosts is not null)
        {
            throw new InvalidDataException(
                $"Plugin hook '{hookId}' matcher field hosts is only valid on HTTP events.");
        }

        return new PluginHookMatcher(
            origins,
            isToolEvent ? ValidateHookPatterns(hookId, matcher.Tools, "tools", IsToolPattern) : [],
            isToolEvent ? ValidateHookPatterns(hookId, matcher.SourceIds, "sourceIds", IsSourceIdPattern) : [],
            isToolEvent ? ValidateHookKinds(hookId, matcher.Kinds) : [],
            isToolEvent ? ValidateHookSources(hookId, matcher.Sources) : [],
            isHttpEvent ? ValidateHookHosts(hookId, matcher.Hosts) : []);
    }

    private static IReadOnlyList<DirectTurnOrigin> ValidateHookOrigins(
        string hookId,
        IReadOnlyList<string?>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' matcher origins must not be empty.");
        }

        var results = new List<DirectTurnOrigin>(values.Count);
        foreach (var value in values)
        {
            results.Add(value switch
            {
                "interactive" => DirectTurnOrigin.Interactive,
                "subagent" => DirectTurnOrigin.Subagent,
                "continuation" => DirectTurnOrigin.Continuation,
                _ => throw new InvalidDataException(
                    $"Plugin hook '{hookId}' matcher origins contains an invalid value.")
            });
        }

        return results;
    }

    private static IReadOnlyList<string> ValidateHookPatterns(
        string hookId,
        IReadOnlyList<string?>? values,
        string fieldName,
        Func<string, bool> isValid)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' matcher {fieldName} must not be empty.");
        }

        var results = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is null || value.Length is 0 or > MaximumHookPatternLength || !isValid(value))
            {
                throw new InvalidDataException(
                    $"Plugin hook '{hookId}' matcher {fieldName} contains an invalid value.");
            }

            results.Add(value);
        }

        return results;
    }

    private static IReadOnlyList<ToolCallKind> ValidateHookKinds(
        string hookId,
        IReadOnlyList<string?>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' matcher kinds must not be empty.");
        }

        var results = new List<ToolCallKind>(values.Count);
        foreach (var value in values)
        {
            results.Add(value switch
            {
                "other" => ToolCallKind.Other,
                "read" => ToolCallKind.Read,
                "edit" => ToolCallKind.Edit,
                "run" => ToolCallKind.Run,
                "search" => ToolCallKind.Search,
                "list" => ToolCallKind.List,
                _ => throw new InvalidDataException(
                    $"Plugin hook '{hookId}' matcher kinds contains an invalid value.")
            });
        }

        return results;
    }

    private static IReadOnlyList<ToolSourceKind> ValidateHookSources(
        string hookId,
        IReadOnlyList<string?>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' matcher sources must not be empty.");
        }

        var results = new List<ToolSourceKind>(values.Count);
        foreach (var value in values)
        {
            results.Add(value switch
            {
                "builtIn" => ToolSourceKind.BuiltIn,
                "mcp" => ToolSourceKind.Mcp,
                "skill" => ToolSourceKind.Skill,
                "plugin" => ToolSourceKind.Plugin,
                _ => throw new InvalidDataException(
                    $"Plugin hook '{hookId}' matcher sources contains an invalid value.")
            });
        }

        return results;
    }

    private static IReadOnlyList<string> ValidateHookHosts(
        string hookId,
        IReadOnlyList<string?>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' matcher hosts must not be empty.");
        }

        var results = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is null || value.Length is 0 or > MaximumHookPatternLength)
            {
                throw InvalidHookHost(hookId);
            }

            if (value.StartsWith("*.", StringComparison.Ordinal))
            {
                var host = value[2..];
                if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    throw InvalidHookHost(hookId);
                }

                results.Add("*." + host.ToLowerInvariant());
                continue;
            }

            // A hook matches against Uri.Host, which reports a canonical literal ("127.0.0.1",
            // "::1"). A rule kept as "127.1", "0x7f.0.0.1" or "[::1]" would install cleanly and
            // then never match, so only the canonical form is stored and bracketed/scoped literals
            // are rejected outright.
            if (value.Contains('[') || value.Contains(']') || value.Contains('%'))
            {
                throw InvalidHookHost(hookId);
            }

            if (IPAddress.TryParse(value, out var address))
            {
                results.Add(address.ToString());
                continue;
            }

            if (Uri.CheckHostName(value) != UriHostNameType.Dns)
            {
                throw InvalidHookHost(hookId);
            }

            results.Add(value.ToLowerInvariant());
        }

        return results;
    }

    private static InvalidDataException InvalidHookHost(string hookId)
        => new($"Plugin hook '{hookId}' matcher hosts contains an invalid value.");

    private static bool IsToolPattern(string value)
        => value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '*');

    private static bool IsSourceIdPattern(string value)
        => value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or '*');

    private static string ValidateHookCommand(string packageRoot, string hookId, string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidDataException($"Plugin hook '{hookId}' must declare a command.");
        }

        PluginCommandTemplate.ValidateCommand(packageRoot, command, $"Plugin hook '{hookId}' command");
        return command;
    }

    private static IReadOnlyList<string> ValidateHookArguments(
        string packageRoot,
        string hookId,
        IReadOnlyList<string?>? arguments)
    {
        if (arguments is null)
        {
            return [];
        }

        var results = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new InvalidDataException($"Plugin hook '{hookId}' arguments must contain only strings.");
            }

            PluginCommandTemplate.Validate(packageRoot, argument, $"Plugin hook '{hookId}' argument");
            results.Add(argument);
        }

        return results;
    }

    private static string? ValidateOptionalFile(string packageRoot, string? relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var path = PluginCommandTemplate.ResolvePackagePath(packageRoot, relativePath, fieldName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Plugin {fieldName} file does not exist.");
        }

        return NormalizeRelativePath(packageRoot, path);
    }

    private static string NormalizeRelativePath(string packageRoot, string path)
        => Path.GetRelativePath(packageRoot, path).Replace('\\', '/');

    private static void ValidateId(string? id, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            id.Any(character => character is >= 'A' and <= 'Z'))
        {
            throw new InvalidDataException($"{fieldName} must use lowercase ASCII letters, digits, and '-'.");
        }
    }
}
