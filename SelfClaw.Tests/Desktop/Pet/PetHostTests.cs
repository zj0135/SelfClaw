using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetHostTests
{
    [Fact]
    public async Task Failed_loading_does_not_commit_or_show_and_can_be_retried()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings());
        var window = new FakePetWindowAdapter();
        var fail = true;
        await using var host = CreateHost(root.Path, repository, window,
            _ => fail ? throw new InvalidOperationException("broken sprite") : CreateBitmap());
        await FluentActions.Awaiting(() => host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Show)))
            .Should().ThrowAsync<InvalidOperationException>();
        var failed = await host.GetStateAsync();
        failed.IsVisible.Should().BeFalse();
        failed.Settings.Enabled.Should().BeFalse();
        failed.LoadStatus.Should().Be("failed");
        failed.LoadError.Should().Contain("broken sprite");
        fail = false;
        var retried = await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Show));
        retried.IsVisible.Should().BeTrue();
        retried.ActualPetId.Should().Be(PetPackageCatalog.DefaultBuiltInPetId);
        retried.LoadStatus.Should().Be("ready");
    }

    [Fact]
    public async Task Save_failure_keeps_the_confirmed_configuration_and_visible_window()
    {
        using var root = new TemporaryPetRoot();
        var repository = new InMemoryPetSettingsRepository(new PetSettings());
        var window = new FakePetWindowAdapter();
        await using var host = CreateHost(root.Path, repository, window);
        await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Show));
        repository.SaveError = new IOException("settings locked");
        await FluentActions.Awaiting(() => host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Hide)))
            .Should().ThrowAsync<IOException>();
        var state = await host.GetStateAsync();
        state.IsVisible.Should().BeTrue();
        state.Settings.Enabled.Should().BeTrue();
        state.LoadError.Should().Contain("settings locked");
    }

    [Fact]
    public async Task Fallback_reports_the_requested_and_actual_packages_separately_while_hidden()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(root.Path, "broken");
        var repository = new InMemoryPetSettingsRepository(new PetSettings());
        await using var host = CreateHost(root.Path, repository, new FakePetWindowAdapter(), path =>
            path.Contains("broken", StringComparison.Ordinal) ? throw new IOException("broken package") : CreateBitmap());
        var result = await host.ExecuteAsync(new PetHostCommand(PetHostCommandKind.SelectBuiltInPet, "broken"));
        result.Settings.SpriteSheetPath.Should().Be("broken");
        result.SelectedBuiltInPetId.Should().Be("broken");
        result.ActualPetId.Should().Be(PetPackageCatalog.DefaultBuiltInPetId);
        result.LoadStatus.Should().Be("fallback");
        result.LoadError.Should().Contain("broken package");
        result.IsVisible.Should().BeFalse();
    }

    [Fact]
    public Task Decoding_does_not_block_the_dispatcher_and_the_real_view_model_installs_frozen_frames()
        => SelfClaw.Tests.TestDoubles.WpfDispatcherTest.RunAsync(async () =>
        {
            using var root = new TemporaryPetRoot();
            CreatePackage(root.Path, PetPackageCatalog.DefaultBuiltInPetId);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var uiThread = Environment.CurrentManagedThreadId;
            var decodeThread = 0;
            var catalog = new PetPackageCatalog(root.Path, new FakePetSpriteDecoder(_ =>
            {
                decodeThread = Environment.CurrentManagedThreadId;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decoder was not released.");
                return CreateBitmap();
            }), NullLogger<PetPackageCatalog>.Instance);
            var loading = catalog.LoadAsync(new PetSettings(), CancellationToken.None);
            await entered.Task;
            try
            {
                await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { });
                loading.IsCompleted.Should().BeFalse();
                decodeThread.Should().NotBe(uiThread);
            }
            finally { release.Set(); }
            var package = await loading;
            using var viewModel = new PetViewModel(NullLogger<PetViewModel>.Instance, null);
            viewModel.Install(new PetSettings(), package);
            viewModel.CurrentFrame.Should().NotBeNull();
            viewModel.CurrentFrame?.IsFrozen.Should().BeTrue();
        });

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
        await Task.WhenAny(window.ShowStarted.Task, showTask);
        if (showTask.IsFaulted) await showTask;
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
        IPetWindowAdapter window,
        Func<string, System.Windows.Media.Imaging.BitmapSource>? decode = null)
    {
        CreatePackage(root, PetPackageCatalog.DefaultBuiltInPetId);
        var catalog = new PetPackageCatalog(
            root,
            new FakePetSpriteDecoder(decode ?? (_ => CreateBitmap())),
            NullLogger<PetPackageCatalog>.Instance);
        return new PetHost(repository, window, catalog, NullLogger<PetHost>.Instance);
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateBitmap()
    {
        var grid = PetLayout.CreateDefaultGrid();
        var width = grid.Cols * 4;
        var height = grid.Rows * 4;
        return System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
    }

    private static void CreatePackage(string root, string id)
    {
        var packageDirectory = Path.Combine(root, id);
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllBytes(Path.Combine(packageDirectory, "spritesheet.webp"), [0x01]);
        File.WriteAllText(Path.Combine(packageDirectory, "pet.json"), System.Text.Json.JsonSerializer.Serialize(
            new { id, grid = new { cols = 8, rows = 9, cellWidth = 4, cellHeight = 4 } }));
    }
}
