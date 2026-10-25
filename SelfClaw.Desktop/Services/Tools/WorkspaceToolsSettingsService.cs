using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Infrastructure.Processes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services.Tools;

/// <summary>
/// Owns the "which shell does run_shell_command use" setting in desktop-settings.json, and resolves the
/// availability of each selectable shell. Persistence keeps only the kind: the executable is re-resolved
/// per use so an install or uninstall is picked up without restart. An unreadable or invalid stored node
/// falls back to Windows PowerShell, matching the normalization-on-save contract the settings page relies
/// on (illegal input is silently corrected by the host, never surfaced as an error).
/// </summary>
public sealed class WorkspaceToolsSettingsService : IWorkspaceShellPreferenceSource
{
    private const string SettingsNodeName = "workspace_tools";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkspaceShellPreference _preference = new();
    private bool _loaded;

    public WorkspaceToolsSettingsService(DesktopSettingsJsonStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    /// <summary>Raised after a committed change; the payload is the new preference.</summary>
    public event Action<WorkspaceShellPreference>? Changed;

    public async Task<WorkspaceShellPreference> GetPreferenceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return _preference;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceShellPreference> SaveAsync(
        WorkspaceShellKind shellKind,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_preference.ShellKind == shellKind)
            {
                return _preference;
            }

            var next = new WorkspaceShellPreference(shellKind);
            await _settingsStore.WriteNodeAsync(SettingsNodeName, next, JsonOptions, cancellationToken).ConfigureAwait(false);
            _preference = next;
            NotifyChanged();
            return _preference;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<WorkspaceShellAvailability> GetShells()
    {
        List<WorkspaceShellAvailability> shells = [];
        foreach (var kind in Enum.GetValues<WorkspaceShellKind>())
        {
            var resolvedPath = kind switch
            {
                WorkspaceShellKind.WindowsPowerShell => ShellExecutableLocator.TryFindWindowsPowerShell(),
                WorkspaceShellKind.PowerShellSeven => ShellExecutableLocator.TryFindPowerShellSeven(),
                WorkspaceShellKind.CommandPrompt => ShellExecutableLocator.TryFindCommandPrompt(),
                WorkspaceShellKind.GitBash => ShellExecutableLocator.TryFindGitBash(),
                _ => null,
            };
            shells.Add(new WorkspaceShellAvailability(kind, resolvedPath is not null, resolvedPath ?? string.Empty));
        }

        return shells;
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _preference = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task<WorkspaceShellPreference> LoadCoreAsync(CancellationToken cancellationToken)
    {
        WorkspaceShellPreference? stored;
        try
        {
            stored = await _settingsStore
                .ReadNodeAsync<WorkspaceShellPreference>(SettingsNodeName, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A hand-edited or schema-drifted node is not worth failing the app over; the default shell
            // keeps the tool working and the next save rewrites the node.
            return new WorkspaceShellPreference();
        }

        // The enum converter accepts any numeric value, so an out-of-range kind (e.g. a hand-edited 47)
        // must be rejected explicitly rather than flow into the runner's switch.
        return stored is { } preference && Enum.IsDefined(preference.ShellKind)
            ? preference
            : new WorkspaceShellPreference();
    }

    private void NotifyChanged()
    {
        foreach (var subscriber in Changed?.GetInvocationList().Cast<Action<WorkspaceShellPreference>>() ?? [])
        {
            subscriber(_preference);
        }
    }
}
