using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class CapabilityContentCacheTests
{
    [Fact]
    public async Task Unchanged_packages_are_read_exactly_once()
    {
        using var cache = CreateCache();
        var package = CreatePackage();
        var reads = new ReadCounter();

        var first = await cache.GetInstructionBodyAsync(
            package, "instructions.md", reads.TrackBody("body"), CancellationToken.None);
        var second = await cache.GetInstructionBodyAsync(
            package, "instructions.md", reads.TrackBody("body"), CancellationToken.None);

        first.Should().Be("body");
        second.Should().Be("body");
        reads.Value.Should().Be(1);
    }

    [Fact]
    public async Task Changed_package_identity_is_read_again()
    {
        using var cache = CreateCache();
        var reads = new ReadCounter();

        await cache.GetManifestAsync(CreatePackage(contentHash: "sha256:a"), reads.TrackManifest(), CancellationToken.None);
        await cache.GetManifestAsync(CreatePackage(contentHash: "sha256:b"), reads.TrackManifest(), CancellationToken.None);
        await cache.GetManifestAsync(CreatePackage(version: "2.0.0"), reads.TrackManifest(), CancellationToken.None);

        reads.Value.Should().Be(3);
    }

    [Fact]
    public async Task Health_and_binding_notifications_keep_unchanged_package_content()
    {
        var notifier = new ExtensionStateChangeNotifier();
        using var cache = new CapabilityContentCache();
        var package = CreatePackage();
        var reads = new ReadCounter();

        await cache.GetSkillMetadataAsync(package, "SKILL.md", reads.TrackSkill(), CancellationToken.None);
        notifier.Advance();
        await cache.GetSkillMetadataAsync(package, "SKILL.md", reads.TrackSkill(), CancellationToken.None);

        reads.Value.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_read_is_not_cached_and_a_later_turn_reads_again()
    {
        using var cache = CreateCache();
        var package = CreatePackage();
        var reads = new ReadCounter();

        var act = () => cache.GetInstructionBodyAsync(
            package,
            "instructions.md",
            _ => throw new InvalidDataException("unreadable"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();

        var recovered = await cache.GetInstructionBodyAsync(
            package, "instructions.md", reads.TrackBody("body"), CancellationToken.None);

        recovered.Should().Be("body");
        reads.Value.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canceling_one_caller_does_not_cancel_or_evict_the_shared_read(bool cancelFirst)
    {
        using var cache = CreateCache();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        CancellationToken readToken = default;
        Task<string> ReadAsync(CancellationToken token)
        {
            reads++;
            readToken = token;
            return source.Task.WaitAsync(token);
        }

        var package = CreatePackage();
        var first = cache.GetInstructionBodyAsync(package, "body.md", ReadAsync, firstCancellation.Token);
        var second = cache.GetInstructionBodyAsync(package, "body.md", ReadAsync, secondCancellation.Token);
        var canceled = cancelFirst ? first : second;
        var surviving = cancelFirst ? second : first;
        (cancelFirst ? firstCancellation : secondCancellation).Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
            readToken.IsCancellationRequested.Should().BeFalse();
            surviving.IsCompleted.Should().BeFalse();
            source.SetResult("body");
            (await surviving.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("body");
            (await cache.GetInstructionBodyAsync(package, "body.md", ReadAsync, CancellationToken.None)).Should().Be("body");
            reads.Should().Be(1);
        }
        finally
        {
            source.TrySetResult("body");
            try { await Task.WhenAll(first, second); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Disposing_the_cache_cancels_its_shared_reads()
    {
        var cache = CreateCache();
        CancellationToken readToken = default;
        var reading = cache.GetInstructionBodyAsync(CreatePackage(), "body.md", async token =>
        {
            readToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "body";
        }, CancellationToken.None);

        cache.Dispose();

        readToken.IsCancellationRequested.Should().BeTrue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        cache.Dispose();
    }

    [Fact]
    public async Task A_late_failure_cannot_remove_a_replacement_cache_entry()
    {
        using var cache = new CapabilityContentCache();
        var package = CreatePackage();
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = cache.GetInstructionBodyAsync(package, "body.md", _ => source.Task, CancellationToken.None);
        for (var index = 0; index < 128; index++)
        {
            await cache.GetInstructionBodyAsync(package, $"body-{index}.md", _ => Task.FromResult("body"), CancellationToken.None);
        }
        var reads = new ReadCounter();
        await cache.GetInstructionBodyAsync(package, "body.md", reads.TrackBody("new body"), CancellationToken.None);
        source.SetException(new IOException("old read failed"));
        await Assert.ThrowsAsync<IOException>(() => failed);

        (await cache.GetInstructionBodyAsync(package, "body.md", reads.TrackBody("new body"), CancellationToken.None))
            .Should().Be("new body");
        reads.Value.Should().Be(1);
    }

    [Fact]
    public async Task Identical_packages_at_different_install_paths_are_read_independently()
    {
        using var cache = CreateCache();
        var package = CreatePackage();
        var moved = package with { InstallPath = package.InstallPath + "-new" };

        await cache.GetManifestAsync(package, new ReadCounter().TrackManifest(), CancellationToken.None);
        var reads = new ReadCounter();
        await cache.GetManifestAsync(moved, reads.TrackManifest(), CancellationToken.None);

        reads.Value.Should().Be(1);
    }

    private static CapabilityContentCache CreateCache() => new();

    private static ExtensionPackageRecord CreatePackage(
        string version = "1.0.0",
        string contentHash = "sha256:a")
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            ExtensionKind.Plugin,
            "office",
            "Office",
            version,
            "Plugin",
            Path.Combine(Path.GetTempPath(), "SelfClawTests", "office"),
            contentHash,
            "{}",
            null,
            true,
            null,
            null,
            now,
            now);
    }

    private sealed class ReadCounter
    {
        public int Value { get; private set; }

        public Func<CancellationToken, Task<PluginManifest>> TrackManifest()
            => _ =>
            {
                Value++;
                return Task.FromResult(new PluginManifest(
                    1,
                    "office",
                    "Office",
                    "1.0.0",
                    "Plugin",
                    null,
                    [],
                    new PluginContributions(null, [], [], [])));
            };

        public Func<CancellationToken, Task<string>> TrackBody(string body)
            => _ =>
            {
                Value++;
                return Task.FromResult(body);
            };

        public Func<CancellationToken, Task<SkillPackageMetadata>> TrackSkill()
            => _ =>
            {
                Value++;
                return Task.FromResult(new SkillPackageMetadata(
                    "slides",
                    "Slides",
                    "Create slides",
                    "1.0.0",
                    [],
                    "# slides"));
            };
    }
}
