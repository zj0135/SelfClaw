using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Tools;

public sealed class WorkspaceToolsSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Fact]
    public async Task Missing_node_falls_back_to_windows_powershell()
    {
        var service = CreateService();

        (await service.GetPreferenceAsync()).Should().Be(new WorkspaceShellPreference(WorkspaceShellKind.WindowsPowerShell));
    }

    [Fact]
    public async Task Saving_persists_the_kind_and_a_fresh_service_reads_it_back()
    {
        var service = CreateService();
        await service.SaveAsync(WorkspaceShellKind.GitBash);

        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"workspace_tools\"");
        (await service.GetPreferenceAsync()).ShellKind.Should().Be(WorkspaceShellKind.GitBash);
        (await CreateService().GetPreferenceAsync()).ShellKind.Should().Be(WorkspaceShellKind.GitBash);
    }

    [Fact]
    public async Task Saving_the_same_kind_neither_writes_nor_notifies()
    {
        var service = CreateService();
        await service.SaveAsync(WorkspaceShellKind.CommandPrompt);
        var notified = 0;
        service.Changed += _ => notified++;
        File.Delete(SettingsPath);

        (await service.SaveAsync(WorkspaceShellKind.CommandPrompt)).ShellKind.Should().Be(WorkspaceShellKind.CommandPrompt);
        File.Exists(SettingsPath).Should().BeFalse();
        notified.Should().Be(0);
    }

    [Fact]
    public async Task Saving_a_different_kind_notifies_with_the_new_preference()
    {
        var service = CreateService();
        WorkspaceShellPreference? observed = null;
        service.Changed += preference => observed = preference;

        await service.SaveAsync(WorkspaceShellKind.PowerShellSeven);

        observed.Should().Be(new WorkspaceShellPreference(WorkspaceShellKind.PowerShellSeven));
    }

    [Fact]
    public async Task A_hand_edited_invalid_node_falls_back_to_the_default()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, """{ "workspace_tools": { "shellKind": 47 } }""");

        (await CreateService().GetPreferenceAsync()).Should().Be(new WorkspaceShellPreference());
    }

    [Fact]
    public void GetShells_lists_every_kind_with_an_availability_flag()
    {
        var shells = CreateService().GetShells();

        shells.Select(shell => shell.Kind).Should().Equal(
            WorkspaceShellKind.WindowsPowerShell,
            WorkspaceShellKind.PowerShellSeven,
            WorkspaceShellKind.CommandPrompt,
            WorkspaceShellKind.GitBash);
        shells.Should().OnlyContain(shell => shell.Available == (shell.ResolvedPath.Length > 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WorkspaceToolsSettingsService CreateService()
        => new(new DesktopSettingsJsonStore(
            StoragePathDefaults.Create(_root, Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))));
}
