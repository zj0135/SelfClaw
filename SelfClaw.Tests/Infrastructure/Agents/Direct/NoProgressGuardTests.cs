using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// The no-progress guard decides a repeat from a fingerprint of the calls and their results. A result may now
/// carry the image itself, so the bytes have to decide the fingerprint through their digest: a repeat must
/// still be recognized, two different images must not look alike, and no audio-sized base64 string may be built.
/// </summary>
public sealed class NoProgressGuardTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public void An_identical_image_result_repeats_the_fingerprint_until_the_guard_trips()
    {
        var guard = new NoProgressGuard();
        var calls = Calls("call-1");

        for (var batch = 1; batch < NoProgressGuard.RepeatedBatchLimit; batch++)
        {
            guard.Observe(calls, ImageResults(PngBytes)).Should().BeFalse();
            guard.RepeatedBatches.Should().Be(batch);
        }

        guard.Observe(calls, ImageResults(PngBytes)).Should().BeTrue(
            "the same image read again and again is exactly the loop this guard exists to stop");
    }

    [Fact]
    public void Different_image_bytes_never_repeat_the_fingerprint()
    {
        var guard = new NoProgressGuard();

        for (var index = 0; index < NoProgressGuard.RepeatedBatchLimit + 2; index++)
        {
            var bytes = (byte[])PngBytes.Clone();
            bytes[^1] = (byte)index;
            guard.Observe(Calls("call-1"), ImageResults(bytes)).Should().BeFalse();
            guard.RepeatedBatches.Should().Be(1, "a different image is progress, not a repeat");
        }
    }

    [Fact]
    public void A_plain_text_result_still_repeats_the_fingerprint()
    {
        var guard = new NoProgressGuard();
        var calls = Calls("call-1");
        AIContent[] results = [new FunctionResultContent("call-1", "same output")];

        for (var batch = 1; batch < NoProgressGuard.RepeatedBatchLimit; batch++)
        {
            guard.Observe(calls, results).Should().BeFalse();
        }

        guard.Observe(calls, results).Should().BeTrue();
    }

    private static FunctionCallContent[] Calls(string callId)
        => [new(callId, "read_file", new Dictionary<string, object?> { ["relativePath"] = "shot.png" })];

    private static AIContent[] ImageResults(byte[] bytes)
        =>
        [
            new FunctionResultContent("call-1",
                new List<AIContent>
                {
                    new TextContent("Read image 'shot.png' (image/png). The image follows."),
                    new DataContent(bytes, "image/png") { Name = "shot.png" }
                })
        ];
}
