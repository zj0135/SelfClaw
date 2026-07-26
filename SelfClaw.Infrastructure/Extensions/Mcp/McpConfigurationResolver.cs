using System.Net;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpConfigurationResolver
{
    private const string UnavailableConfiguration = "MCP server configuration is unavailable.";
    private const string UnavailableCredentials = "MCP server credentials could not be resolved.";

    private readonly ISecretProtector _secretProtector;
    private readonly StoragePaths _storagePaths;

    public McpConfigurationResolver(ISecretProtector secretProtector, StoragePaths storagePaths)
    {
        _secretProtector = secretProtector;
        _storagePaths = storagePaths;
    }

    public async Task<ResolvedMcpServerConfiguration> ResolveAsync(
        McpServerConfigRecord record,
        string? workspacePath,
        string? pluginRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        McpServerSettings settings;
        try
        {
            settings = ExtensionCatalog.DeserializeSettings(record.SettingsJson);
        }
        catch (JsonException)
        {
            return Unavailable(record, UnavailableConfiguration, workspacePath);
        }

        var environment = new Dictionary<string, string>(settings.Environment, StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, string>(settings.Headers, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var credential in record.CredentialRefs)
            {
                var secret = await _secretProtector.RetrieveSecretAsync(credential.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (secret is null || !TryApplyCredential(credential.Key, secret, environment, headers))
                {
                    return Unavailable(record, UnavailableCredentials, workspacePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unavailable(record, UnavailableCredentials, workspacePath);
        }

        if (!HasRequiredSettings(settings, record.CredentialRefs, environment, headers))
        {
            return Unavailable(record, "MCP server requires additional configuration.", workspacePath);
        }

        var normalizedWorkspace = NormalizeOptionalPath(workspacePath);
        if (settings.RequiresWorkspace && normalizedWorkspace is null)
        {
            return Unavailable(record, "MCP server requires an active workspace.", null);
        }

        return record.Transport switch
        {
            McpTransportKind.Stdio => ResolveStdio(
                record,
                settings,
                environment,
                normalizedWorkspace,
                NormalizeOptionalPath(pluginRoot)),
            McpTransportKind.Http => ResolveHttp(record, settings, headers, normalizedWorkspace),
            _ => Unavailable(record, UnavailableConfiguration, normalizedWorkspace)
        };
    }

    private ResolvedMcpServerConfiguration ResolveStdio(
        McpServerConfigRecord record,
        McpServerSettings settings,
        IReadOnlyDictionary<string, string> environment,
        string? workspacePath,
        string? pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(settings.Command))
        {
            return Unavailable(record, UnavailableConfiguration, workspacePath);
        }

        var command = ExpandTemplate(settings.Command, workspacePath, pluginRoot);
        var arguments = settings.Arguments
            .Select(argument => ExpandTemplate(argument, workspacePath, pluginRoot))
            .ToArray();
        if (command is null || arguments.Any(argument => argument is null))
        {
            return Unavailable(record, UnavailableConfiguration, workspacePath);
        }

        var workingDirectory = settings.WorkingDirectoryMode switch
        {
            "workspace" => workspacePath,
            "plugin" => pluginRoot,
            "appData" => _storagePaths.AppDataDirectory,
            _ => null
        };
        if (workingDirectory is null)
        {
            var reason = settings.WorkingDirectoryMode is "workspace"
                ? "MCP server requires an active workspace."
                : settings.WorkingDirectoryMode is "plugin"
                    ? "MCP server plugin directory is unavailable."
                    : UnavailableConfiguration;
            return Unavailable(record, reason, workspacePath);
        }

        return new ResolvedMcpServerConfiguration(
            record.Id,
            record.DisplayName,
            record.Transport,
            record.ConfigRevision,
            record.SourcePluginId,
            true,
            null,
            command,
            arguments.Select(argument => argument!).ToArray(),
            workingDirectory,
            environment,
            null,
            null,
            null,
            new Dictionary<string, string>(),
            workspacePath);
    }

    private static ResolvedMcpServerConfiguration ResolveHttp(
        McpServerConfigRecord record,
        McpServerSettings settings,
        IReadOnlyDictionary<string, string> headers,
        string? workspacePath)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            settings.ConnectionTimeoutSeconds is null or <= 0)
        {
            return Unavailable(record, UnavailableConfiguration, workspacePath);
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !endpoint.IsLoopback &&
            (!IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address)))
        {
            return Unavailable(record, "Remote MCP endpoints must use HTTPS.", workspacePath);
        }

        return new ResolvedMcpServerConfiguration(
            record.Id,
            record.DisplayName,
            record.Transport,
            record.ConfigRevision,
            record.SourcePluginId,
            true,
            null,
            null,
            [],
            null,
            new Dictionary<string, string>(),
            endpoint,
            settings.TransportMode,
            TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds.Value),
            headers,
            workspacePath);
    }

    private static bool TryApplyCredential(
        string path,
        string value,
        IDictionary<string, string> environment,
        IDictionary<string, string> headers)
    {
        const string environmentPrefix = "environment.";
        const string headersPrefix = "headers.";
        if (path.StartsWith(environmentPrefix, StringComparison.Ordinal) && path.Length > environmentPrefix.Length)
        {
            environment[path[environmentPrefix.Length..]] = value;
            return true;
        }

        if (path.StartsWith(headersPrefix, StringComparison.Ordinal) && path.Length > headersPrefix.Length)
        {
            headers[path[headersPrefix.Length..]] = value;
            return true;
        }

        return false;
    }

    private static bool HasRequiredSettings(
        McpServerSettings settings,
        IReadOnlyDictionary<string, string> credentialRefs,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var path in settings.RequiredFieldNames ?? [])
        {
            if (path.StartsWith("environment.", StringComparison.Ordinal))
            {
                var key = path["environment.".Length..];
                if ((!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) &&
                    !credentialRefs.ContainsKey(path))
                {
                    return false;
                }
            }
            else if (path.StartsWith("headers.", StringComparison.Ordinal))
            {
                var key = path["headers.".Length..];
                if ((!headers.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) &&
                    !credentialRefs.ContainsKey(path))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string? ExpandTemplate(string value, string? workspacePath, string? pluginRoot)
    {
        var expanded = value;
        if (expanded.Contains("${pluginRoot}", StringComparison.Ordinal))
        {
            if (pluginRoot is null)
            {
                return null;
            }

            expanded = expanded.Replace("${pluginRoot}", pluginRoot, StringComparison.Ordinal);
        }

        if (expanded.Contains("${workspaceRoot}", StringComparison.Ordinal))
        {
            if (workspacePath is null)
            {
                return null;
            }

            expanded = expanded.Replace("${workspaceRoot}", workspacePath, StringComparison.Ordinal);
        }

        return expanded.Contains("${", StringComparison.Ordinal) ? null : expanded;
    }

    private static string? NormalizeOptionalPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static ResolvedMcpServerConfiguration Unavailable(
        McpServerConfigRecord record,
        string reason,
        string? workspacePath)
        => new(
            record.Id,
            record.DisplayName,
            record.Transport,
            record.ConfigRevision,
            record.SourcePluginId,
            false,
            reason,
            null,
            [],
            null,
            new Dictionary<string, string>(),
            null,
            null,
            null,
            new Dictionary<string, string>(),
            workspacePath);
}
