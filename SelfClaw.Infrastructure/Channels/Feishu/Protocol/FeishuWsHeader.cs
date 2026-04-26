namespace SelfClaw.Infrastructure.Channels.Feishu;

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
