using System.Net;
using System.Text.Json;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Processes;

namespace SelfClaw.Infrastructure.Extensions.Plugins;

internal sealed class PluginManifestReader
{
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

        return new PluginManifest(
            1,
            raw.Id,
            raw.Name.Trim(),
            raw.Version.Trim(),
            raw.Description?.Trim() ?? string.Empty,
            raw.Publisher?.Trim(),
            new PluginContributions(directInstructions, skills, mcpServers));
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
