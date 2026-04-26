namespace SelfClaw.Infrastructure.Channels.Feishu;

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
            {
                return header.Value;
            }
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
            FeishuProto.WriteBytes(stream, 5, header.ToArray());
        }

        FeishuProto.WriteString(stream, 6, PayloadEncoding);
        FeishuProto.WriteString(stream, 7, PayloadType);

        if (Payload.Length > 0)
        {
            FeishuProto.WriteBytes(stream, 8, Payload);
        }

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
