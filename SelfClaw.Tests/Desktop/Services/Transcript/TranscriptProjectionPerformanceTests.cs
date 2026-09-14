using SelfClaw.Desktop.Services.Transcript.Views;
using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Infrastructure.Options;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptProjectionPerformanceTests
{
    private readonly ITestOutputHelper _output;
    public TranscriptProjectionPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Updating_one_message_preserves_history_items_and_allocates_in_proportion_to_history_count()
    {
        var small = Measure(500);
        var large = Measure(2500);
        large.Should().BeLessThan(small * 8);
    }

    private long Measure(int count)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = Guid.NewGuid();
        var messages = new MessageRecord[count];
        var tools = new ToolExecutionRecord[count];
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid();
            var toolId = Guid.NewGuid();
            messages[index] = new(id, conversation, MessageRole.Assistant, "answer", MessageStatus.Completed, now.AddSeconds(index), now,
                Segments: [new(id, 0, MessageSegmentKind.ToolCall, null, toolId), new(id, 1, MessageSegmentKind.Text, "answer", null)]);
            tools[index] = new(toolId, conversation, "read_file", "{}", ToolExecutionStatus.Completed, "read", null, 1, now, now,
                MessageId: id, ResultContent: "stable output");
        }
        var projection = new TranscriptProjection(StoragePathDefaults.CreateDefault());
        var request = new TranscriptProjectionRequest(messages, tools, [], [], conversation, false, false, null, "direct", "build", "Build", 0, "require-approval");
        var original = projection.Build(request) ?? throw new InvalidOperationException("Missing initial projection.");
        var allocations = new List<long>();
        var timings = new List<double>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            messages[^1] = messages[^1] with { MarkdownContent = $"updated {iteration}",
                Segments = [(messages[^1].Segments ?? throw new InvalidOperationException("Missing segments."))[0], new(messages[^1].Id, 1, MessageSegmentKind.Text, $"updated {iteration}", null)] };
            var start = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var state = projection.Build(request) ?? throw new InvalidOperationException("Changed content was not projected.");
            timer.Stop();
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - start);
            timings.Add(timer.Elapsed.TotalMilliseconds);
            state.Items[0].Should().BeSameAs(original.Items[0]);
            state.Items[^1].Segments.Should().Contain(segment => segment.Markdown == $"updated {iteration}");
        }
        var allocated = allocations.Order().ElementAt(2);
        _output.WriteLine($"{count} messages / tools: median {timings.Order().ElementAt(2):F2} ms; {allocated:N0} bytes.");
        return allocated;
    }
}
