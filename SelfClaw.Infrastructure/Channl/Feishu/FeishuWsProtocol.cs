using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SelfClaw.Infrastructure.Channl.Feishu;

internal static class FeishuWsConstants
{
    public const string EndpointPath = "/callback/ws/endpoint";
    public const string DeviceId = "device_id";
    public const string ServiceId = "service_id";

    public const string HeaderTimestamp = "timestamp";
    public const string HeaderType = "type";
    public const string HeaderMessageId = "message_id";
    public const string HeaderSum = "sum";
    public const string HeaderSeq = "seq";
    public const string HeaderTraceId = "trace_id";
    public const string HeaderInstanceId = "instance_id";
    public const string HeaderBizRt = "biz_rt";
    public const string HandshakeStatus = "Handshake-Status";
    public const string HandshakeMessage = "Handshake-Msg";
    public const string HandshakeAuthErrorCode = "Handshake-Autherrcode";

    public const string MessageTypeEvent = "event";
    public const string MessageTypeCard = "card";
    public const string MessageTypePing = "ping";
    public const string MessageTypePong = "pong";

    public const int FrameTypeControl = 0;
    public const int FrameTypeData = 1;

    public const int ResponseOk = 0;
    public const int SystemBusy = 1;
    public const int Forbidden = 403;
    public const int AuthFailed = 514;
    public const int ExceedConnectionLimit = 1000040350;
    public const int InternalError = 1000040343;
}

internal sealed class FeishuWsFrame
{
    public ulong SeqId { get; set; }
    public ulong LogId { get; set; }
    public int Service { get; set; }
    public int Method { get; set; }
    public List<FeishuWsHeader> Headers { get; } = [];
    public string PayloadEncoding { get; set; } = string.Empty;
    public string PayloadType { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public string LogIdNew { get; set; } = string.Empty;

    public string GetHeader(string key)
    {
        foreach (var header in Headers)
        {
            if (string.Equals(header.Key, key, StringComparison.Ordinal))
                return header.Value;
        }

        return string.Empty;
    }

    public int GetHeaderInt(string key)
    {
        return int.TryParse(GetHeader(key), out var value) ? value : 0;
    }

    public void SetHeader(string key, string value)
    {
        foreach (var header in Headers)
        {
            if (string.Equals(header.Key, key, StringComparison.Ordinal))
            {
                header.Value = value;
                return;
            }
        }

        Headers.Add(new FeishuWsHeader(key, value));
    }

    public byte[] ToArray()
    {
        using var stream = new MemoryStream();
        FeishuProto.WriteUInt64(stream, 1, SeqId);
        FeishuProto.WriteUInt64(stream, 2, LogId);
        FeishuProto.WriteInt32(stream, 3, Service);
        FeishuProto.WriteInt32(stream, 4, Method);

        foreach (var header in Headers)
        {
            FeishuProto.WriteMessage(stream, 5, header.ToArray());
        }

        FeishuProto.WriteString(stream, 6, PayloadEncoding);
        FeishuProto.WriteString(stream, 7, PayloadType);

        if (Payload.Length > 0)
            FeishuProto.WriteBytes(stream, 8, Payload);

        FeishuProto.WriteString(stream, 9, LogIdNew);
        return stream.ToArray();
    }

    public static FeishuWsFrame Parse(ReadOnlySpan<byte> bytes)
    {
        var frame = new FeishuWsFrame();
        var offset = 0;

        while (offset < bytes.Length)
        {
            var tag = FeishuProto.ReadVarint32(bytes, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);

            switch (fieldNumber)
            {
                case 1:
                    frame.SeqId = FeishuProto.ReadVarint64(bytes, ref offset);
                    break;
                case 2:
                    frame.LogId = FeishuProto.ReadVarint64(bytes, ref offset);
                    break;
                case 3:
                    frame.Service = checked((int)FeishuProto.ReadVarint64(bytes, ref offset));
                    break;
                case 4:
                    frame.Method = checked((int)FeishuProto.ReadVarint64(bytes, ref offset));
                    break;
                case 5:
                    frame.Headers.Add(FeishuWsHeader.Parse(FeishuProto.ReadBytes(bytes, ref offset)));
                    break;
                case 6:
                    frame.PayloadEncoding = FeishuProto.ReadString(bytes, ref offset);
                    break;
                case 7:
                    frame.PayloadType = FeishuProto.ReadString(bytes, ref offset);
                    break;
                case 8:
                    frame.Payload = FeishuProto.ReadBytes(bytes, ref offset).ToArray();
                    break;
                case 9:
                    frame.LogIdNew = FeishuProto.ReadString(bytes, ref offset);
                    break;
                default:
                    FeishuProto.SkipField(bytes, ref offset, wireType);
                    break;
            }
        }

        return frame;
    }

    public static FeishuWsFrame CreatePing(int serviceId)
    {
        var frame = new FeishuWsFrame
        {
            Service = serviceId,
            Method = FeishuWsConstants.FrameTypeControl
        };
        frame.Headers.Add(new FeishuWsHeader(FeishuWsConstants.HeaderType, FeishuWsConstants.MessageTypePing));
        return frame;
    }
}

internal sealed class FeishuWsHeader(string key, string value)
{
    public string Key { get; set; } = key;
    public string Value { get; set; } = value;

    public byte[] ToArray()
    {
        using var stream = new MemoryStream();
        FeishuProto.WriteString(stream, 1, Key);
        FeishuProto.WriteString(stream, 2, Value);
        return stream.ToArray();
    }

    public static FeishuWsHeader Parse(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        var key = string.Empty;
        var value = string.Empty;

        while (offset < bytes.Length)
        {
            var tag = FeishuProto.ReadVarint32(bytes, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);

            switch (fieldNumber)
            {
                case 1:
                    key = FeishuProto.ReadString(bytes, ref offset);
                    break;
                case 2:
                    value = FeishuProto.ReadString(bytes, ref offset);
                    break;
                default:
                    FeishuProto.SkipField(bytes, ref offset, wireType);
                    break;
            }
        }

        return new FeishuWsHeader(key, value);
    }
}

internal sealed class FeishuWsClientConfig
{
    public int ReconnectCount { get; set; } = -1;
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromMinutes(2);
    public int ReconnectNonceSeconds { get; set; } = 30;
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromMinutes(2);

    public static FeishuWsClientConfig Parse(JsonElement element)
    {
        return new FeishuWsClientConfig
        {
            ReconnectCount = FeishuJson.GetInt32(element, "ReconnectCount") ?? -1,
            ReconnectInterval = TimeSpan.FromSeconds(
                Math.Max(1, FeishuJson.GetInt32(element, "ReconnectInterval") ?? 120)),
            ReconnectNonceSeconds = Math.Max(0, FeishuJson.GetInt32(element, "ReconnectNonce") ?? 30),
            PingInterval = TimeSpan.FromSeconds(
                Math.Max(1, FeishuJson.GetInt32(element, "PingInterval") ?? 120))
        };
    }
}

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
            return;

        WriteBytes(stream, fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    public static void WriteMessage(Stream stream, int fieldNumber, byte[] payload)
    {
        WriteBytes(stream, fieldNumber, payload);
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
            throw new InvalidOperationException("Invalid protobuf varint32.");
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
                return result;

            shift += 7;
            if (shift >= 70)
                throw new InvalidOperationException("Invalid protobuf varint.");
        }

        throw new InvalidOperationException("Unexpected end of protobuf stream.");
    }

    public static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var length64 = ReadVarint64(bytes, ref offset);
        if (length64 > int.MaxValue)
            throw new InvalidOperationException("Protobuf field is too large.");

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
            throw new InvalidOperationException("Unexpected end of protobuf payload.");
    }
}

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
            return payload;

        _segments[sequence] = payload;
        LastUpdatedUtc = DateTimeOffset.UtcNow;

        var combinedLength = 0;
        foreach (var segment in _segments)
        {
            if (segment is null)
                return null;

            combinedLength += segment.Length;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(combinedLength);
        try
        {
            var offset = 0;
            foreach (var segment in _segments)
            {
                Buffer.BlockCopy(segment!, 0, buffer, offset, segment!.Length);
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

internal sealed class FeishuLongConnectionException(string message, bool isClientFault, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsClientFault { get; } = isClientFault;
    public int? Code { get; } = code;
}
