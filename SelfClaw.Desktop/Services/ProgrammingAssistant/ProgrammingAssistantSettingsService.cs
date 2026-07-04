using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

public sealed partial class ProgrammingAssistantSettingsService
{
    private const string SettingsNodeName = "programming_assistant";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyList<ProgrammingCliDefinition> CliDefinitions =
    [
        new(
            Id: "claude",
            Kind: CliAgentKind.Claude,
            Name: "Claude Code",
            Vendor: "Anthropic official CLI",
            Commands: ["claude"],
            VersionArguments: ["--version"],
            Models: ["Default (CLI config)"],
            ReasoningLevels: []),
        new(
            Id: "codex",
            Kind: CliAgentKind.Codex,
            Name: "Codex CLI",
            Vendor: "OpenAI official CLI",
            Commands: ["codex"],
            VersionArguments: ["--version"],
            Models: ["Default (CLI config)"],
            ReasoningLevels: ["Default (CLI config)", "Low", "Medium", "High"]),
        new(
            Id: "opencode",
            Kind: CliAgentKind.OpenCode,
            Name: "OpenCode",
            Vendor: "Open-source agent CLI",
            Commands: ["opencode"],
            VersionArguments: ["--version"],
            Models: ["Default (CLI config)"],
            ReasoningLevels: []),
    ];

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProgrammingAssistantSettings _settings = new();
    private bool _loaded;

    public ProgrammingAssistantSettingsService(DesktopSettingsJsonStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public string? SelectedCliId => _settings.SelectedCliId;

    /// <summary>
    /// Reads the persisted settings without ever scanning. Startup seeds them via
    /// <see cref="GetOrInitializeAsync"/>, so readers (composer selector, settings page load,
    /// chat turns) get the detection result from config instead of re-probing PATH.
    /// </summary>
    public async Task<ProgrammingAssistantSettings> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolves the <see cref="CliAgentKind"/> of the currently selected CLI, or <c>null</c> when no
    /// detected CLI is selected. This is what a chat turn passes to the runtime as the target agent.
    /// </summary>
    public async Task<CliAgentKind?> GetSelectedCliKindAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return settings.Tools
            .FirstOrDefault(tool => string.Equals(tool.Id, settings.SelectedCliId, StringComparison.OrdinalIgnoreCase))
            ?.Kind;
    }

    public async Task<ProgrammingAssistantSettings> GetOrInitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!_settings.HasScanned)
            {
                _settings = CreateSettings(await ScanCoreAsync(cancellationToken).ConfigureAwait(false), selectedCliId: null);
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProgrammingAssistantSettings> RescanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _settings = CreateSettings(
                await ScanCoreAsync(cancellationToken).ConfigureAwait(false),
                _settings.SelectedCliId);
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProgrammingAssistantSettings> SelectCliAsync(string? cliId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            var normalized = NormalizeCliId(cliId);
            if (normalized is not null && !_settings.Tools.Any(tool => string.Equals(tool.Id, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                normalized = null;
            }

            _settings = _settings with { SelectedCliId = normalized };
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _settings = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task<ProgrammingAssistantSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        return await _settingsStore
                   .ReadNodeAsync<ProgrammingAssistantSettings>(SettingsNodeName, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? new ProgrammingAssistantSettings();
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await _settingsStore.WriteNodeAsync(SettingsNodeName, _settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static ProgrammingAssistantSettings CreateSettings(
        IReadOnlyList<DetectedProgrammingCli> tools,
        string? selectedCliId)
    {
        var normalizedSelected = NormalizeCliId(selectedCliId);
        if (normalizedSelected is null || !tools.Any(tool => string.Equals(tool.Id, normalizedSelected, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedSelected = tools.FirstOrDefault()?.Id;
        }

        return new ProgrammingAssistantSettings
        {
            HasScanned = true,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            SelectedCliId = normalizedSelected,
            Tools = tools
        };
    }

    private static async Task<IReadOnlyList<DetectedProgrammingCli>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var detected = new List<DetectedProgrammingCli>();

        foreach (var definition in CliDefinitions)
        {
            var result = await ScanDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                detected.Add(result);
            }
        }

        return detected;
    }

    private static async Task<DetectedProgrammingCli?> ScanDefinitionAsync(
        ProgrammingCliDefinition definition,
        CancellationToken cancellationToken)
    {
        foreach (var command in definition.Commands)
        {
            var resolver = new CliCommandResolver();
            try
            {
                var invocation = resolver.Resolve(command, definition.VersionArguments);
                var rawVersion = await ReadVersionAsync(invocation, cancellationToken).ConfigureAwait(false);
                var version = NormalizeVersion(command, rawVersion);

                return new DetectedProgrammingCli(
                    definition.Id,
                    definition.Kind,
                    definition.Name,
                    definition.Vendor,
                    version,
                    definition.Models,
                    definition.ReasoningLevels);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                Debug.WriteLine(exception);
            }
        }

        return null;
    }

    private static async Task<string?> ReadVersionAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(VersionTimeout);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = invocation.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        if (invocation.IsShellWrapped)
        {
            process.StartInfo.Arguments = invocation.VerbatimArguments;
        }
        else
        {
            foreach (var argument in invocation.ArgumentList)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
        }

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string NormalizeVersion(string command, string? rawVersion)
    {
        var version = rawVersion?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (string.IsNullOrWhiteSpace(version))
        {
            return "已安装";
        }

        return VersionRegex().IsMatch(version)
            ? version
            : $"{command} {version}";
    }

    private static string? NormalizeCliId(string? cliId)
    {
        var normalized = cliId?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex VersionRegex();

    private sealed record ProgrammingCliDefinition(
        string Id,
        CliAgentKind Kind,
        string Name,
        string Vendor,
        IReadOnlyList<string> Commands,
        IReadOnlyList<string> VersionArguments,
        IReadOnlyList<string> Models,
        IReadOnlyList<string> ReasoningLevels);
}

public sealed record ProgrammingAssistantSettings
{
    public bool HasScanned { get; init; }

    public DateTimeOffset? ScannedAtUtc { get; init; }

    public string? SelectedCliId { get; init; }

    public IReadOnlyList<DetectedProgrammingCli> Tools { get; init; } = [];
}

public sealed record DetectedProgrammingCli(
    string Id,
    CliAgentKind Kind,
    string Name,
    string Vendor,
    string Version,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ReasoningLevels);
