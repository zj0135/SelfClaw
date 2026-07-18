using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
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
            // Static list — Claude accepts these via `--effort <level>` per its `--help` output. Sentinel
            // first so "Default (CLI config)" means "omit the flag and let the CLI decide".
            ReasoningLevels: [DefaultModelOption, "low", "medium", "high", "xhigh", "max"],
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

    public string? SelectedReasoningLevel => _settings.SelectedReasoningLevel;

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
    /// Resolves the currently selected CLI together with the model and reasoning effort the user picked for
    /// it, or <c>null</c> when no detected CLI is selected. This is what a chat turn passes to the runtime.
    /// <see cref="CliInvocationSelection.Model"/> / <see cref="CliInvocationSelection.ReasoningEffort"/> are
    /// already <c>null</c> for "use the CLI's own default" (the persisted values collapse the Default
    /// sentinel to null), so callers can pass them straight through to the argument builder.
    /// </summary>
    public async Task<CliInvocationSelection?> GetSelectedInvocationAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var tool = settings.Tools
            .FirstOrDefault(candidate => string.Equals(candidate.Id, settings.SelectedCliId, StringComparison.OrdinalIgnoreCase));

        return tool is null
            ? null
            : new CliInvocationSelection(tool.Kind, settings.SelectedModel, settings.SelectedReasoningLevel);
    }

    public async Task<ProgrammingAssistantSettings> GetOrInitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!_settings.HasScanned)
            {
                _settings = CreateSettings(await ScanCoreAsync(cancellationToken).ConfigureAwait(false), selectedCliId: null, selectedModel: null, selectedReasoningLevel: null);
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
                _settings.SelectedModel,
                _settings.SelectedReasoningLevel);
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

            // Switching CLIs invalidates the remembered model and reasoning effort (the new CLI has a
            // different catalogue), so drop both; re-selecting the same CLI keeps whatever was chosen.
            var sameCli = string.Equals(normalized, _settings.SelectedCliId, StringComparison.OrdinalIgnoreCase);

            _settings = _settings with
            {
                SelectedCliId = normalized,
                SelectedModel = sameCli ? _settings.SelectedModel : null,
                SelectedReasoningLevel = sameCli ? _settings.SelectedReasoningLevel : null,
            };
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the model the user picked for the currently selected CLI. Kept only when it belongs to that
    /// CLI's discovered catalogue; blank input, the Default sentinel, a stale value, or an unknown model all
    /// collapse to <c>null</c> so the turn falls back to the CLI's own default.
    /// </summary>
    public async Task<ProgrammingAssistantSettings> SelectModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _settings = _settings with { SelectedModel = ResolveCatalogSelection(model, SelectedTool()?.Models) };
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the reasoning effort the user picked for the currently selected CLI (only Codex advertises
    /// these). Same contract as <see cref="SelectModelAsync"/>: the value is kept only when it belongs to the
    /// CLI's reasoning catalogue, otherwise it collapses to <c>null</c> (use the CLI's own default).
    /// </summary>
    public async Task<ProgrammingAssistantSettings> SelectReasoningLevelAsync(string? reasoningLevel, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _settings = _settings with { SelectedReasoningLevel = ResolveCatalogSelection(reasoningLevel, SelectedTool()?.ReasoningLevels) };
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Live-probes a single CLI by rerunning its version command against PATH (same launch semantics as the
    /// startup scan). Returns a success/version/error triple so the UI can show a real toast instead of a
    /// hard-coded "connected" banner. Not gated by <see cref="_gate"/>: the probe is read-only and independent
    /// of the persisted settings state, and blocking on the mutex would stall it behind an in-flight rescan.
    /// </summary>
    public async Task<CliTestResult> TestCliAsync(string? cliId, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCliId(cliId);
        var definition = CliDefinitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return new CliTestResult(cliId ?? string.Empty, Success: false, Version: null, Error: "未识别的 CLI");
        }

        Exception? lastError = null;
        foreach (var command in definition.Commands)
        {
            var resolver = new CliCommandResolver();
            try
            {
                var invocation = resolver.Resolve(command, definition.VersionArguments);
                var rawVersion = await ReadVersionAsync(invocation, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawVersion))
                {
                    // Timed out or no output — try the next command name, otherwise fall through to the "not found" reply.
                    continue;
                }

                return new CliTestResult(definition.Id, Success: true, Version: NormalizeVersion(command, rawVersion), Error: null);
            }
            catch (FileNotFoundException)
            {
                // This command name isn't on PATH; try the next alias.
                continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastError = exception;
                Debug.WriteLine(exception);
            }
        }

        return new CliTestResult(
            definition.Id,
            Success: false,
            Version: null,
            Error: lastError?.Message ?? "未在 PATH 中检测到该 CLI");
    }

    private DetectedProgrammingCli? SelectedTool() =>
        _settings.Tools.FirstOrDefault(tool => string.Equals(tool.Id, _settings.SelectedCliId, StringComparison.OrdinalIgnoreCase));

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
        string? selectedModel,
        string? selectedReasoningLevel)
    {
        var normalizedSelected = NormalizeCliId(selectedCliId);
        if (normalizedSelected is null || !tools.Any(tool => string.Equals(tool.Id, normalizedSelected, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedSelected = tools.FirstOrDefault()?.Id;
        }

        // Keep the remembered model / reasoning effort only when each still belongs to the resolved CLI's
        // catalogue — a rescan may have changed the lists (or the selected CLI itself), invalidating them.
        var selectedTool = tools.FirstOrDefault(tool => string.Equals(tool.Id, normalizedSelected, StringComparison.OrdinalIgnoreCase));

        return new ProgrammingAssistantSettings
        {
            HasScanned = true,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            SelectedCliId = normalizedSelected,
            SelectedModel = ResolveCatalogSelection(selectedModel, selectedTool?.Models),
            SelectedReasoningLevel = ResolveCatalogSelection(selectedReasoningLevel, selectedTool?.ReasoningLevels),
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
    /// Resolves a user selection (model or reasoning level) to its stored form: <c>null</c> when blank, when
    /// it is the <see cref="DefaultModelOption"/> sentinel ("use the CLI's own default"), or when it is not
    /// part of <paramref name="catalog"/>; otherwise the trimmed, verbatim value. Storing <c>null</c> for the
    /// default keeps the settings file clean and lets the argument builder treat "non-null" as "pass this
    /// flag". Values are matched case-sensitively (CLI slugs like <c>opencode/mimo-v2.5-free</c> are verbatim).
    /// </summary>
    private static string? ResolveCatalogSelection(string? value, IReadOnlyList<string>? catalog)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || string.Equals(normalized, DefaultModelOption, StringComparison.OrdinalIgnoreCase)
            || catalog is null
            || !catalog.Contains(normalized, StringComparer.Ordinal))
        {
            return null;
        }

        return normalized;
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
