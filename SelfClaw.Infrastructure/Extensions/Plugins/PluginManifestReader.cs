using System.Text.Json;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

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
        var permissions = (raw.Permissions ?? [])
            .Select(permission => permission?.Trim() ?? string.Empty)
            .ToArray();
        if (permissions.Any(permission => string.IsNullOrWhiteSpace(permission) ||
                                          permission.Any(character =>
                                              !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-')))
        {
            throw new InvalidDataException("Plugin permissions contain an invalid value.");
        }

        if (permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Length)
        {
            throw new InvalidDataException("Plugin permissions must be unique.");
        }

        return new PluginManifest(
            1,
            raw.Id,
            raw.Name.Trim(),
            raw.Version.Trim(),
            raw.Description?.Trim() ?? string.Empty,
            raw.Publisher?.Trim(),
            permissions.OrderBy(permission => permission, StringComparer.Ordinal).ToArray(),
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

            var path = ResolvePackagePath(packageRoot, skill.Path, "Skill path");
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
                ValidateTemplateValue(packageRoot, server.Command, "MCP command");
                foreach (var argument in arguments)
                {
                    ValidateTemplateValue(packageRoot, argument, "MCP argument");
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

                if (setting.Target == "env" ? !IsValidEnvironmentKey(key) : !IsValidHeaderName(key))
                {
                    throw new InvalidDataException($"Plugin MCP '{server.Id}' required setting key '{key}' is invalid.");
                }

                if (!requiredSettingPaths.Add($"{setting.Target}.{key}"))
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

        var path = ResolvePackagePath(packageRoot, relativePath, fieldName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Plugin {fieldName} file does not exist.");
        }

        return NormalizeRelativePath(packageRoot, path);
    }

    private static void ValidateTemplateValue(string packageRoot, string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var remainder = value
            .Replace("${pluginRoot}", string.Empty, StringComparison.Ordinal)
            .Replace("${workspaceRoot}", string.Empty, StringComparison.Ordinal);
        if (remainder.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{fieldName} contains an unsupported template variable.");
        }

        // The DLL ban applies to every value, not just ${pluginRoot}-prefixed ones: a bare relative
        // "server/entry.dll" is the same declaration with the prefix omitted.
        if (string.Equals(Path.GetExtension(value.TrimEnd()), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plugin DLL entry points are not supported.");
        }

        // Existence is only checkable for package-relative values; a bare command such as "node" is
        // resolved from PATH at launch and must stay legal.
        if (value.Contains("${pluginRoot}", StringComparison.Ordinal))
        {
            var relative = value.Replace("${pluginRoot}", string.Empty, StringComparison.Ordinal)
                .TrimStart('/', '\\');
            var path = ResolvePackagePath(packageRoot, relative, fieldName);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidDataException($"{fieldName} references a missing package entry.");
            }
        }
    }

    private static string ResolvePackagePath(string packageRoot, string relativePath, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{fieldName} must be package-relative.");
        }

        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{fieldName} escapes the package root.");
        }

        return candidate;
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

    private static bool IsValidEnvironmentKey(string key)
        => (char.IsAsciiLetter(key[0]) || key[0] == '_') &&
           key.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsValidHeaderName(string name)
        => name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

}
