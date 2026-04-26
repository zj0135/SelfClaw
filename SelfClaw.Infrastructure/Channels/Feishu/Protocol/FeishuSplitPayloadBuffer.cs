using System.Buffers;

namespace SelfClaw.Infrastructure.Channels.Feishu;

internal sealed class FeishuSplitPayloadBuffer
{
    private readonly byte[][] _segments;

    public FeishuSplitPayloadBuffer(int totalSegments)
    {
        _segments = new byte[totalSegments][];
        LastUpdatedUtc = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset LastUpdatedUtc { get; private set; }

    public byte[]? AddSegment(int sequence, byte[] payload)
    {
        if (sequence < 0 || sequence >= _segments.Length)
        {
            return payload;
        }

        _segments[sequence] = payload;
        LastUpdatedUtc = DateTimeOffset.UtcNow;

        var combinedLength = 0;
        foreach (var segment in _segments)
        {
            if (segment is null)
            {
                return null;
            }

            combinedLength += segment.Length;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(combinedLength);
        try
        {
            var offset = 0;
            foreach (var segment in _segments)
            {
                Buffer.BlockCopy(segment!, 0, buffer, offset, segment.Length);
                offset += segment.Length;
            }

            var combined = new byte[combinedLength];
            Buffer.BlockCopy(buffer, 0, combined, 0, combinedLength);
            return combined;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
