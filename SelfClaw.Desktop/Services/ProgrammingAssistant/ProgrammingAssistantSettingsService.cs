using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Discovery;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

public sealed partial class ProgrammingAssistantSettingsService
{
    private const string SettingsNodeName = "programming_assistant";

    /// <summary>
    /// The sentinel option meaning "let the CLI use whatever model its own config selects". Always the
    /// first entry so the default behaviour is preserved even when a live catalogue is discovered.
    /// </summary>
    private const string DefaultModelOption = "Default (CLI config)";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Listing a CLI's models can be slower than a version probe (Codex builds a full model catalogue,
    /// OpenCode may reach out to models.dev), so discovery gets its own, more generous budget.
    /// </summary>
    private static readonly TimeSpan ModelDiscoveryTimeout = TimeSpan.FromSeconds(8);
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
            // Claude Code has no "list models" command, so this catalogue is maintained by hand. Keep the
            // Default sentinel first, then the stable CLI aliases (`claude --model <alias>`).
            Models: [DefaultModelOption, "opus", "sonnet", "haiku"],
            ReasoningLevels: [],
            ModelListArguments: null,
            ParseModels: null,
            ParseReasoningLevels: null),
        new(
            Id: "codex",
            Kind: CliAgentKind.Codex,
            Name: "Codex CLI",
            Vendor: "OpenAI official CLI",
            Commands: ["codex"],
            VersionArguments: ["--version"],
            Models: [DefaultModelOption],
            // Fallback only: the live list (incl. xhigh) is derived from codex debug models' per-model
            // supported_reasoning_levels; this mirrors it for when discovery is unavailable.
            ReasoningLevels: [DefaultModelOption, "low", "medium", "high", "xhigh"],
            ModelListArguments: ["debug", "models"],
            ParseModels: CliModelListParser.ParseCodexDebugModels,
            ParseReasoningLevels: CliModelListParser.ParseCodexReasoningLevels),
        new(
            Id: "opencode",
            Kind: CliAgentKind.OpenCode,
            Name: "OpenCode",
            Vendor: "Open-source agent CLI",
            Commands: ["opencode"],
            VersionArguments: ["--version"],
            Models: [DefaultModelOption],
            ReasoningLevels: [],
            ModelListArguments: ["models"],
            ParseModels: CliModelListParser.ParseOpenCodeModels,
            ParseReasoningLevels: null),
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

    public string? SelectedModel => _settings.SelectedModel;

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
                _settings = CreateSettings(await ScanCoreAsync(cancellationToken).ConfigureAwait(false), selectedCliId: null, selectedModel: null);
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
                _settings.SelectedCliId,
                _settings.SelectedModel);
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

            // Switching CLIs invalidates the remembered model (the new CLI has a different catalogue), so
            // drop it; re-selecting the same CLI keeps whatever model was already chosen.
            var selectedModel = string.Equals(normalized, _settings.SelectedCliId, StringComparison.OrdinalIgnoreCase)
                ? _settings.SelectedModel
                : null;

            _settings = _settings with { SelectedCliId = normalized, SelectedModel = selectedModel };
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the model the user picked for the currently selected CLI. The value is kept only when it
    /// belongs to that CLI's discovered catalogue; anything else (blank, stale, or an unknown model) resets
    /// to <c>null</c> so the turn falls back to the CLI's own default.
    /// </summary>
    public async Task<ProgrammingAssistantSettings> SelectModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            var normalized = NormalizeModel(model);
            if (normalized is not null)
            {
                var selectedTool = _settings.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.Id, _settings.SelectedCliId, StringComparison.OrdinalIgnoreCase));
                if (selectedTool is null || !selectedTool.Models.Contains(normalized, StringComparer.Ordinal))
                {
                    normalized = null;
                }
            }

            _settings = _settings with { SelectedModel = normalized };
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
        string? selectedCliId,
        string? selectedModel)
    {
        var normalizedSelected = NormalizeCliId(selectedCliId);
        if (normalizedSelected is null || !tools.Any(tool => string.Equals(tool.Id, normalizedSelected, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedSelected = tools.FirstOrDefault()?.Id;
        }

        // Keep the remembered model only when it still belongs to the resolved CLI's catalogue — a rescan
        // may have changed the list (or the selected CLI itself), leaving the old choice invalid.
        var selectedTool = tools.FirstOrDefault(tool => string.Equals(tool.Id, normalizedSelected, StringComparison.OrdinalIgnoreCase));
        var normalizedModel = NormalizeModel(selectedModel);
        if (normalizedModel is not null && (selectedTool is null || !selectedTool.Models.Contains(normalizedModel, StringComparer.Ordinal)))
        {
            normalizedModel = null;
        }

        return new ProgrammingAssistantSettings
        {
            HasScanned = true,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            SelectedCliId = normalizedSelected,
            SelectedModel = normalizedModel,
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
                var catalog = await DiscoverCatalogAsync(resolver, command, definition, cancellationToken).ConfigureAwait(false);

                return new DetectedProgrammingCli(
                    definition.Id,
                    definition.Kind,
                    definition.Name,
                    definition.Vendor,
                    version,
                    catalog.Models,
                    catalog.ReasoningLevels);
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

    /// <summary>
    /// Runs the CLI's "list models" command (when it has one) and derives its model catalogue — and, for
    /// CLIs that report it, the reasoning levels — from a single invocation, merging each list behind the
    /// <see cref="DefaultModelOption"/> sentinel. Any failure — no discovery command, a timeout, an
    /// unparseable response, or an empty result — falls back to the definition's static lists so a live CLI
    /// is never left without a usable list.
    /// </summary>
    private static async Task<CliCatalog> DiscoverCatalogAsync(
        CliCommandResolver resolver,
        string command,
        ProgrammingCliDefinition definition,
        CancellationToken cancellationToken)
    {
        var fallback = new CliCatalog(definition.Models, definition.ReasoningLevels);
        if (definition.ModelListArguments is null || definition.ParseModels is null)
        {
            return fallback;
        }

        try
        {
            var invocation = resolver.Resolve(command, definition.ModelListArguments);
            var output = await RunProcessAsync(invocation, ModelDiscoveryTimeout, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                return fallback;
            }

            var stdout = output.Value.StandardOutput;

            var discoveredModels = definition.ParseModels(stdout);
            var models = discoveredModels.Count == 0 ? definition.Models : MergeWithDefault(discoveredModels);

            var reasoningLevels = definition.ReasoningLevels;
            if (definition.ParseReasoningLevels is not null)
            {
                var discoveredReasoning = definition.ParseReasoningLevels(stdout);
                if (discoveredReasoning.Count > 0)
                {
                    reasoningLevels = MergeWithDefault(discoveredReasoning);
                }
            }

            return new CliCatalog(models, reasoningLevels);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or OperationCanceledException)
        {
            Debug.WriteLine(exception);
            return fallback;
        }
    }

    /// <summary>
    /// Prepends the <see cref="DefaultModelOption"/> sentinel to a discovered list, dropping any duplicate
    /// the CLI may already have emitted so the sentinel appears exactly once and first.
    /// </summary>
    private static IReadOnlyList<string> MergeWithDefault(IReadOnlyList<string> discovered)
    {
        var models = new List<string>(discovered.Count + 1) { DefaultModelOption };
        models.AddRange(discovered.Where(model =>
            !string.Equals(model, DefaultModelOption, StringComparison.OrdinalIgnoreCase)));
        return models;
    }

    private static async Task<string?> ReadVersionAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync(invocation, VersionTimeout, cancellationToken).ConfigureAwait(false);
        if (output is not { } result)
        {
            return null;
        }

        // Some CLIs print their version banner to stderr; prefer stdout but accept stderr as a fallback.
        return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }

    /// <summary>
    /// Starts <paramref name="invocation"/>, captures both output streams, and returns them once the
    /// process exits, or <c>null</c> when it exceeds <paramref name="timeout"/> (the process is killed).
    /// Shared by the version probe and model discovery so both honour the same launch/kill semantics.
    /// </summary>
    private static async Task<ProcessOutput?> RunProcessAsync(
        CommandInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

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
            return new ProcessOutput(output, error);
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

    /// <summary>
    /// Trims a model selection to a stored form, collapsing blank input to <c>null</c>. Model ids are kept
    /// case-sensitive (CLI slugs like <c>opencode/mimo-v2.5-free</c> are matched verbatim against the
    /// discovered catalogue), unlike CLI ids which are lower-cased.
    /// </summary>
    private static string? NormalizeModel(string? model)
    {
        var normalized = model?.Trim();
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
        IReadOnlyList<string> ReasoningLevels,
        IReadOnlyList<string>? ModelListArguments,
        Func<string?, IReadOnlyList<string>>? ParseModels,
        Func<string?, IReadOnlyList<string>>? ParseReasoningLevels);

    private readonly record struct ProcessOutput(string StandardOutput, string StandardError);

    private readonly record struct CliCatalog(IReadOnlyList<string> Models, IReadOnlyList<string> ReasoningLevels);
}

public sealed record ProgrammingAssistantSettings
{
    public bool HasScanned { get; init; }

    public DateTimeOffset? ScannedAtUtc { get; init; }

    public string? SelectedCliId { get; init; }

    /// <summary>
    /// The model the user picked for <see cref="SelectedCliId"/> in the composer, or <c>null</c> to let the
    /// CLI use its own configured default. Persisted so the choice survives a restart; reset to <c>null</c>
    /// whenever the selected CLI changes (its catalogue no longer applies).
    /// </summary>
    public string? SelectedModel { get; init; }

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
