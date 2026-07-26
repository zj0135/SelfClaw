using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetHostTests
{
    [Fact]
    public async Task Initialize_restores_an_enabled_pet_once()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings { Enabled = true });
        var window = new FakePetWindowAdapter();
        var host = CreateHost(root.Path, repository, window);

        await host.InitializeAsync();
        var state = await host.GetStateAsync();

        window.ShowCount.Should().Be(1);
        state.IsVisible.Should().BeTrue();
        state.Settings.Enabled.Should().BeTrue();
        repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Visibility_commands_keep_window_and_persisted_state_in_sync()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings());
        var window = new FakePetWindowAdapter();
        var host = CreateHost(root.Path, repository, window);

        var shown = await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Show));
        var hidden = await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Hide));
        var toggled = await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Toggle));

        shown.Should().Match<PetHostState>(state => state.IsVisible && state.Settings.Enabled);
        hidden.Should().Match<PetHostState>(state => !state.IsVisible && !state.Settings.Enabled);
        toggled.Should().Match<PetHostState>(state => state.IsVisible && state.Settings.Enabled);
        repository.SaveCount.Should().Be(3);
    }

    [Fact]
    public async Task SelectBuiltInPet_persists_catalog_identity_and_reloads_the_window_adapter()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(root.Path, PetPackageCatalog.DefaultBuiltInPetId);
        CreatePackage(root.Path, "test-pet");
        var repository = new InMemoryPetSettingsRepository(new PetSettings
        {
            Grid = new GridConfig { Cols = 3, Rows = 2 },
        });
        var window = new FakePetWindowAdapter();
        var host = CreateHost(root.Path, repository, window);

        var state = await host.ExecuteAsync(
            new PetHostCommand(PetHostCommandKind.SelectBuiltInPet, "test-pet"));

        state.SelectedBuiltInPetId.Should().Be("test-pet");
        state.Settings.SpriteSheetPath.Should().Be("test-pet");
        state.Settings.Grid.Should().BeNull();
        window.ReloadedSettings.Should().ContainSingle().Which.Should().Be(state.Settings);
        repository.Settings.Should().Be(state.Settings);
    }

    [Fact]
    public async Task Placement_notifications_are_persisted_behind_the_host_interface()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings { Enabled = true });
        var window = new FakePetWindowAdapter();
        var host = CreateHost(root.Path, repository, window);
        await host.GetStateAsync();

        window.CommitPlacement(new PetPlacement(120.5, 80.25, "DISPLAY-2"));
        var state = await host.GetStateAsync();

        state.Settings.OffsetX.Should().Be(120.5);
        state.Settings.OffsetY.Should().Be(80.25);
        state.Settings.ScreenDeviceName.Should().Be("DISPLAY-2");
        repository.Settings.Should().Be(state.Settings);
    }

    [Fact]
    public async Task Commands_are_serialized_while_an_adapter_operation_is_in_flight()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings());
        var window = new FakePetWindowAdapter
        {
            ShowStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueShow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var host = CreateHost(root.Path, repository, window);

        var showTask = host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Show));
        await window.ShowStarted.Task;
        var hideTask = host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Hide));

        window.HideCount.Should().Be(0);
        window.ContinueShow.SetResult(true);
        await Task.WhenAll(showTask, hideTask);

        window.HideCount.Should().Be(1);
        repository.Settings.Enabled.Should().BeFalse();
    }

    private static PetHost CreateHost(
        string root,
        IPetSettingsRepository repository,
        IPetWindowAdapter window)
    {
        var catalog = new PetPackageCatalog(
            root,
            new FakePetSpriteDecoder(_ => throw new InvalidOperationException("Decoder is not used by host tests.")),
            NullLogger<PetPackageCatalog>.Instance);
        return new PetHost(repository, window, catalog, NullLogger<PetHost>.Instance);
    }

    private static void CreatePackage(string root, string id)
    {
        var packageDirectory = Path.Combine(root, id);
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllBytes(Path.Combine(packageDirectory, "spritesheet.webp"), [0x01]);
    }
}
