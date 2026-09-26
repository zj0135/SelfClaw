using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class PluginHookExecutionLogTests
{
    [Fact]
    public void GetRecent_returns_newest_first()
    {
        var log = new PluginHookExecutionLog();
        log.Append(Entry("p", "first", DateTimeOffset.UnixEpoch));
        log.Append(Entry("p", "second", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        log.GetRecent("p").Select(item => item.HookId).Should().Equal("second", "first");
    }

    [Fact]
    public void GetRecent_returns_empty_for_an_unknown_plugin()
        => new PluginHookExecutionLog().GetRecent("missing").Should().BeEmpty();

    [Fact]
    public void Append_evicts_the_oldest_entry_beyond_the_cap()
    {
        var log = new PluginHookExecutionLog();
        for (var index = 0; index < 205; index++)
        {
            log.Append(Entry("p", $"hook-{index}", DateTimeOffset.UnixEpoch.AddSeconds(index)));
        }

        var entries = log.GetRecent("p");
        entries.Should().HaveCount(200);
        entries[0].HookId.Should().Be("hook-204");
        entries[^1].HookId.Should().Be("hook-5");
    }

    [Fact]
    public void Append_truncates_detail_and_stderr_tail()
    {
        var log = new PluginHookExecutionLog();
        log.Append(new PluginHookExecutionEntry(
            DateTimeOffset.UnixEpoch, "p", "h", "runCompleted", null, "failed", 1,
            ExitCode: 1,
            Detail: new string('d', 600),
            StderrTail: new string('s', 4096)));

        var entry = log.GetRecent("p").Should().ContainSingle().Subject;
        entry.Detail!.Length.Should().Be(512);
        entry.StderrTail!.Length.Should().BeLessThan(4096);
    }

    [Fact]
    public async Task Append_is_thread_safe()
    {
        var log = new PluginHookExecutionLog();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 200),
            (index, _) =>
            {
                log.Append(Entry("p", $"hook-{index}", DateTimeOffset.UnixEpoch));
                return ValueTask.CompletedTask;
            });

        log.GetRecent("p").Should().HaveCount(200);
    }

    private static PluginHookExecutionEntry Entry(string pluginId, string hookId, DateTimeOffset timestamp)
        => new(timestamp, pluginId, hookId, "toolExecuted", null, "observed", 1, 0, null, null);
}
