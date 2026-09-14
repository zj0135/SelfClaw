using SelfClaw.Desktop.Services.Settings;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Infrastructure.Agents.Cli.Discovery;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed class ProgrammingAssistantSettingsService : IAsyncDisposable
{
    private const string SettingsNodeName = "programming_assistant";

    private const string DefaultModelOption = ProgrammingCliDiscovery.DefaultModelOption;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProgrammingAssistantSettings _settings = new();
    private bool _loaded;
    private readonly IProgrammingCliDiscovery _discovery;
    private readonly ILogger<ProgrammingAssistantSettingsService> _logger;
    private readonly object _scanSync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<ProgrammingAssistantSettings>? _scanTask;
    private int _isScanning;
    private string? _scanError;


    public ProgrammingAssistantSettingsService(DesktopSettingsJsonStore settingsStore, IProgrammingCliDiscovery discovery,
        ILogger<ProgrammingAssistantSettingsService> logger)
    {
        _settingsStore = settingsStore;
        _discovery = discovery;
        _logger = logger;
    }

    public event Action<ProgrammingAssistantSettings>? Changed;
    public bool IsScanning => Volatile.Read(ref _isScanning) != 0;
    public string? ScanError => _scanError;

    /// <summary>
    /// Reads the persisted settings without ever scanning. Startup seeds them via
    /// <see cref="InitializeDiscoveryAsync"/>, so readers (composer selector, settings page load,
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

    public async Task InitializeDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (!(await GetCurrentAsync(cancellationToken).ConfigureAwait(false)).HasScanned)
            await RescanAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ProgrammingAssistantSettings> RescanAsync(CancellationToken cancellationToken = default)
    {
        lock (_scanSync)
        {
            if (_scanTask is null || _scanTask.IsCompleted) _scanTask = ScanAndCommitAsync(cancellationToken);
            return _scanTask.WaitAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task? scan;
        lock (_scanSync) scan = _scanTask;
        if (scan is not null)
            await scan.ContinueWith(completed => {
                if (completed.IsFaulted) _logger.LogError(completed.Exception, "CLI scan failed during shutdown.");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task<ProgrammingAssistantSettings> ScanAndCommitAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        await GetCurrentAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _isScanning, 1);
        _scanError = null;
        NotifyChanged();
        try
        {
            var tools = await _discovery.ScanAsync(token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var next = CreateSettings(tools, _settings.SelectedCliId, _settings.SelectedModel, _settings.SelectedReasoningLevel);
                await SaveCoreAsync(next, token).ConfigureAwait(false);
                return _settings;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { _scanError = exception.Message; throw; }
        finally { Interlocked.Exchange(ref _isScanning, 0); NotifyChanged(); }
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

            var next = _settings with
            {
                SelectedCliId = normalized,
                SelectedModel = sameCli ? _settings.SelectedModel : null,
                SelectedReasoningLevel = sameCli ? _settings.SelectedReasoningLevel : null,
            };
            await SaveCoreAsync(next, cancellationToken).ConfigureAwait(false);
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
            var next = _settings with { SelectedModel = ResolveCatalogSelection(model, SelectedTool()?.Models) };
            await SaveCoreAsync(next, cancellationToken).ConfigureAwait(false);
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
            var next = _settings with { SelectedReasoningLevel = ResolveCatalogSelection(reasoningLevel, SelectedTool()?.ReasoningLevels) };
            await SaveCoreAsync(next, cancellationToken).ConfigureAwait(false);
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
    public Task<CliTestResult> TestCliAsync(string? cliId, CancellationToken cancellationToken = default)
        => _discovery.TestAsync(cliId, cancellationToken);

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

    private async Task SaveCoreAsync(ProgrammingAssistantSettings next, CancellationToken cancellationToken)
    {
        next = next with { Revision = checked(_settings.Revision + 1) };
        await _settingsStore.WriteNodeAsync(SettingsNodeName, next, JsonOptions, cancellationToken).ConfigureAwait(false);
        _settings = next;
        NotifyChanged();
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

    private void NotifyChanged()
    {
        foreach (var subscriber in Changed?.GetInvocationList().Cast<Action<ProgrammingAssistantSettings>>() ?? [])
        {
            try { subscriber(_settings); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Failed to observe committed CLI settings."); }
        }
    }
}
