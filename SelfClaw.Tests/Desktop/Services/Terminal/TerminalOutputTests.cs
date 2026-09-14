using System.Text;
using FluentAssertions;
using SelfClaw.Desktop.Services.Terminal;

namespace SelfClaw.Tests.Desktop.Services.Terminal;

public sealed class TerminalOutputTests
{
    [Fact]
    public async Task Continuous_utf8_survives_a_pipe_read_boundary_inside_every_character()
    {
        const string expected = "中文终端 😀\r\nnext line";
        using var stream = new ByteAtATimeStream(Encoding.UTF8.GetBytes(expected));
        var output = new StringBuilder();
        await new TerminalOutputReader().ReadAsync(stream, text => output.Append(text));
        output.ToString().Should().Be(expected);
    }

    [Fact]
    public void Slow_or_hidden_consumers_have_a_bounded_tail_and_old_sessions_cannot_append_to_a_new_one()
    {
        var buffer = new TerminalOutputBuffer();
        var first = new object();
        buffer.Reset(first);
        for (var i = 0; i < 10000; i++) buffer.Append(first, new string('x', 1024));
        buffer.Length.Should().BeLessThanOrEqualTo(TerminalOutputBuffer.Capacity);
        var text = buffer.Drain();
        text.Should().Contain("discarded");
        text.Length.Should().BeLessThanOrEqualTo(32768);
        var next = new object();
        buffer.Reset(next);
        buffer.Append(first, "stale output");
        buffer.Append(next, "current output");
        buffer.Drain().Should().Be("current output");
    }

    private sealed class ByteAtATimeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
