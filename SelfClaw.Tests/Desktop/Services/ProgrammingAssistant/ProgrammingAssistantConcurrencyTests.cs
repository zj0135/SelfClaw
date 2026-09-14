using SelfClaw.Desktop.Services.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.ProgrammingAssistant;

public sealed class ProgrammingAssistantConcurrencyTests
{
    [Fact]
    public async Task Slow_scan_does_not_block_confirmed_reads_or_overwrite_choices_committed_during_discovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var store = new DesktopSettingsJsonStore(StoragePathDefaults.Create(directory, Path.Combine(directory, "test.db"), Path.Combine(directory, "secrets")));
        var tool = new DetectedProgrammingCli("codex", CliAgentKind.Codex, "Codex", "OpenAI", "1", ["model-a", "model-b"], ["high"]);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        await store.WriteNodeAsync("programming_assistant", new ProgrammingAssistantSettings { HasScanned = true,
            SelectedCliId = "codex", SelectedModel = "model-a", Tools = [tool] }, options);
        var discovery = new DelayedDiscovery();
        await using var service = new ProgrammingAssistantSettingsService(store, discovery, NullLogger<ProgrammingAssistantSettingsService>.Instance);
        try
        {
            var scan = service.RescanAsync();
            await discovery.Started.Task;
            (await service.GetSelectedInvocationAsync().WaitAsync(TimeSpan.FromSeconds(2)))?.Model.Should().Be("model-a");
            await service.SelectModelAsync("model-b").WaitAsync(TimeSpan.FromSeconds(2));
            discovery.Result.SetResult([tool]);
            (await scan).SelectedModel.Should().Be("model-b");
            (await service.GetSelectedInvocationAsync())?.Model.Should().Be("model-b");
        }
        finally { discovery.Result.TrySetResult([tool]); await service.StopAsync(CancellationToken.None); Directory.Delete(directory, true); }
    }

    private sealed class DelayedDiscovery : IProgrammingCliDiscovery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DetectedProgrammingCli>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DetectedProgrammingCli>> ScanAsync(CancellationToken cancellationToken)
        { Started.TrySetResult(); return Result.Task.WaitAsync(cancellationToken); }
        public Task<CliTestResult> TestAsync(string? cliId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
