using System.Text;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class OpenAiToolCallTypeNormalizingStreamTests
{
    [Fact]
    public async Task Empty_tool_call_type_is_replaced_with_function()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"\","
            + "\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]}}]}\n\n";

        var output = await TransformAsync(input);

        output.Should().Contain("\"type\":\"function\"");
        output.Should().NotContain("\"type\":\"\"");
        output.Should().Contain("\"name\":\"read_file\"");
    }

    [Fact]
    public async Task Missing_tool_call_type_is_added()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{}\"}}]}}]}\n\n";

        var output = await TransformAsync(input);

        output.Should().Contain("\"type\":\"function\"");
        output.Should().Contain("\"arguments\":\"{}\"");
    }

    [Fact]
    public async Task Valid_tool_call_type_is_preserved_without_changes()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"type\":\"function\","
            + "\"function\":{\"name\":\"read_file\"}}]}}]}\n\n";

        var output = await TransformAsync(input);

        output.Should().Be(input);
    }

    [Fact]
    public async Task Non_tool_call_frames_are_forwarded_verbatim()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n"
            + "data: [DONE]\n\n";

        var output = await TransformAsync(input);

        output.Should().Be(input);
    }

    [Fact]
    public async Task Chunked_reads_across_buffer_boundaries_are_reassembled()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"\","
            + "\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]}}]}\n\n"
            + "data: [DONE]\n\n";

        var output = await TransformAsync(input, chunkSize: 1);

        output.Should().Be(
            input.Replace("\"type\":\"\"", "\"type\":\"function\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiple_tool_calls_are_all_normalized()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":["
            + "{\"index\":0,\"id\":\"call_1\",\"type\":\"\",\"function\":{\"name\":\"read_file\"}},"
            + "{\"index\":1,\"id\":\"call_2\",\"function\":{\"name\":\"list_files\"}}]}}]}\n\n";

        var output = await TransformAsync(input);

        output.Split("\"type\":\"function\"").Should().HaveCount(3);
        output.Should().Contain("\"name\":\"read_file\"");
        output.Should().Contain("\"name\":\"list_files\"");
    }

    [Fact]
    public async Task Malformed_json_is_forwarded_unchanged()
    {
        const string input = "data: {\"choices\":[{\"tool_calls\":[}\n\n";

        var output = await TransformAsync(input);

        output.Should().Be(input);
    }

    private static async Task<string> TransformAsync(string input, int chunkSize = int.MaxValue)
    {
        await using var source = new ChunkedStream(Encoding.UTF8.GetBytes(input), chunkSize);
        await using var stream = new OpenAiToolCallTypeNormalizingStream(source);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = data.Length - _position;
            var take = Math.Min(Math.Min(remaining, count), chunkSize);
            if (take <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
