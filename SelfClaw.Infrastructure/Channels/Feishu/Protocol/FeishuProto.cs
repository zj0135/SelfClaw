using System.Text;

namespace SelfClaw.Infrastructure.Channels.Feishu;

internal static class FeishuProto
{
    public static void WriteUInt64(Stream stream, int fieldNumber, ulong value)
    {
        WriteTag(stream, fieldNumber, 0);
        WriteVarint64(stream, value);
    }

    public static void WriteInt32(Stream stream, int fieldNumber, int value)
    {
        WriteTag(stream, fieldNumber, 0);
        WriteVarint64(stream, unchecked((uint)value));
    }

    public static void WriteString(Stream stream, int fieldNumber, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        WriteBytes(stream, fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    public static void WriteBytes(Stream stream, int fieldNumber, byte[] payload)
    {
        WriteTag(stream, fieldNumber, 2);
        WriteVarint64(stream, (ulong)payload.Length);
        stream.Write(payload, 0, payload.Length);
    }

    public static uint ReadVarint32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = ReadVarint64(bytes, ref offset);
        if (value > uint.MaxValue)
        {
            throw new InvalidOperationException("Invalid protobuf varint32.");
        }

        return (uint)value;
    }

    public static ulong ReadVarint64(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        var shift = 0;

        while (offset < bytes.Length)
        {
            var current = bytes[offset++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new InvalidOperationException("Invalid protobuf varint.");
            }
        }

        throw new InvalidOperationException("Unexpected end of protobuf stream.");
    }

    public static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var length64 = ReadVarint64(bytes, ref offset);
        if (length64 > int.MaxValue)
        {
            throw new InvalidOperationException("Protobuf field is too large.");
        }

        var length = (int)length64;
        EnsureCanRead(bytes, offset, length);
        var slice = bytes[offset..(offset + length)];
        offset += length;
        return slice;
    }

    public static string ReadString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        return Encoding.UTF8.GetString(ReadBytes(bytes, ref offset));
    }

    public static void SkipField(ReadOnlySpan<byte> bytes, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint64(bytes, ref offset);
                return;
            case 1:
                EnsureCanRead(bytes, offset, 8);
                offset += 8;
                return;
            case 2:
                var length = (int)ReadVarint64(bytes, ref offset);
                EnsureCanRead(bytes, offset, length);
                offset += length;
                return;
            case 5:
                EnsureCanRead(bytes, offset, 4);
                offset += 4;
                return;
            default:
                throw new InvalidOperationException($"Unsupported protobuf wire type: {wireType}");
        }
    }

    private static void WriteTag(Stream stream, int fieldNumber, int wireType)
    {
        WriteVarint64(stream, (ulong)((fieldNumber << 3) | wireType));
    }

    private static void WriteVarint64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        var count = 0;

        while (value >= 0x80)
        {
            buffer[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[count++] = (byte)value;
        stream.Write(buffer[..count]);
    }

    private static void EnsureCanRead(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > bytes.Length)
        {
            throw new InvalidOperationException("Unexpected end of protobuf payload.");
        }
    }
}
