using System.Text;
using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// Line-buffered SSE transform for the OpenAI-compatible stream quirks the OpenAI SDK and
/// Microsoft.Extensions.AI reject or ignore. It guarantees every object inside a streamed
/// <c>tool_calls</c> array carries <c>"type":"function"</c>, and aliases the reasoning field
/// names M.E.AI does not read onto <c>reasoning_content</c>. Only <c>data:</c> frames are
/// inspected; every other byte is forwarded unchanged. See
/// <see cref="OpenAiStreamNormalizingPolicy"/> for why this is needed.
/// </summary>
internal sealed class OpenAiStreamNormalizingStream : Stream
{
    private const string DataPrefix = "data:";
    private const string ToolCallsName = "tool_calls";
    private const string MessageName = "message";
    private const string DeltaName = "delta";
    private const string TypeName = "type";
    private const string TextName = "text";
    private const string SummaryName = "summary";
    private const string ReasoningContentName = "reasoning_content";
    private const string ReasoningName = "reasoning";
    private const string ReasoningTextName = "reasoning_text";
    private const string ReasoningDetailsName = "reasoning_details";
    private readonly Stream _inner;
    private readonly byte[] _readBuffer = new byte[8192];
    private byte[] _pending = new byte[8192];
    private int _pendingLength;
    private byte[] _output = [];
    private int _outputOffset;

    public OpenAiStreamNormalizingStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_outputOffset < _output.Length)
        {
            return Drain(buffer);
        }

        while (true)
        {
            var newlineIndex = Array.IndexOf(_pending, (byte)'\n', 0, _pendingLength);
            if (newlineIndex >= 0)
            {
                var line = Encoding.UTF8.GetString(_pending, 0, newlineIndex + 1);
                Shift(newlineIndex + 1);
                _output = Encoding.UTF8.GetBytes(NormalizeLine(line));
                _outputOffset = 0;
                if (_output.Length > 0)
                {
                    return Drain(buffer);
                }

                continue;
            }

            var read = await _inner.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_pendingLength > 0)
                {
                    var tail = Encoding.UTF8.GetString(_pending, 0, _pendingLength);
                    _pendingLength = 0;
                    _output = Encoding.UTF8.GetBytes(NormalizeLine(tail));
                    _outputOffset = 0;
                    if (_output.Length > 0)
                    {
                        return Drain(buffer);
                    }
                }

                return 0;
            }

            Append(read);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Drain(Memory<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _output.Length - _outputOffset);
        _output.AsSpan(_outputOffset, count).CopyTo(buffer.Span);
        _outputOffset += count;
        return count;
    }

    private void Append(int count)
    {
        if (_pendingLength + count > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + count));
        }

        _readBuffer.AsSpan(0, count).CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += count;
    }

    private void Shift(int count)
    {
        _pendingLength -= count;
        if (_pendingLength > 0)
        {
            Array.Copy(_pending, count, _pending, 0, _pendingLength);
        }
    }

    private static string NormalizeLine(string line)
    {
        if (!line.StartsWith(DataPrefix, StringComparison.Ordinal) ||
            !(line.Contains(ToolCallsName, StringComparison.Ordinal) ||
              line.Contains(ReasoningName, StringComparison.Ordinal)))
        {
            return line;
        }

        var jsonStart = line.IndexOf('{', DataPrefix.Length);
        var jsonEnd = line.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < jsonStart)
        {
            return line;
        }

        try
        {
            using var document = JsonDocument.Parse(line[jsonStart..(jsonEnd + 1)]);
            var changed = false;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteElement(document.RootElement, writer, ref changed);
            }

            return changed
                ? line[..jsonStart] + Encoding.UTF8.GetString(buffer.ToArray()) + line[(jsonEnd + 1)..]
                : line;
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer, ref bool changed, bool isMessage = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, writer, ref changed, isMessage);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer, ref changed);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(JsonElement element, Utf8JsonWriter writer, ref bool changed, bool isMessage)
    {
        writer.WriteStartObject();
        var hasReasoningContent = false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(ToolCallsName) && property.Value.ValueKind == JsonValueKind.Array)
            {
                WriteToolCalls(property, writer, ref changed);
                continue;
            }

            if (property.NameEquals(ReasoningContentName))
            {
                hasReasoningContent = IsNonEmptyString(property.Value);
            }

            writer.WritePropertyName(property.Name);
            WriteElement(
                property.Value,
                writer,
                ref changed,
                property.NameEquals(DeltaName) || property.NameEquals(MessageName));
        }

        if (isMessage && !hasReasoningContent && TryReadReasoningText(element, out var reasoningText))
        {
            writer.WriteString(ReasoningContentName, reasoningText);
            changed = true;
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads reasoning text out of the field names OpenAI-compatible providers use instead of
    /// <c>reasoning_content</c>. The first source that yields text wins, so a provider mirroring the
    /// same text into several fields is not duplicated into a doubled thinking block.
    /// </summary>
    private static bool TryReadReasoningText(JsonElement message, out string reasoningText)
    {
        if (TryReadNonEmptyString(message, ReasoningName, out reasoningText) ||
            TryReadNonEmptyString(message, ReasoningTextName, out reasoningText))
        {
            return true;
        }

        if (message.TryGetProperty(ReasoningDetailsName, out var details) &&
            details.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryReadNonEmptyString(detail, TextName, out var text) ||
                    TryReadNonEmptyString(detail, SummaryName, out text))
                {
                    builder.Append(text);
                }
            }

            if (builder.Length > 0)
            {
                reasoningText = builder.ToString();
                return true;
            }
        }

        reasoningText = string.Empty;
        return false;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && IsNonEmptyString(property))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsNonEmptyString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(element.GetString());

    private static void WriteToolCalls(JsonProperty property, Utf8JsonWriter writer, ref bool changed)
    {
        writer.WritePropertyName(property.Name);
        writer.WriteStartArray();
        foreach (var toolCall in property.Value.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
            {
                WriteElement(toolCall, writer, ref changed);
                continue;
            }

            writer.WriteStartObject();
            var hasType = false;
            foreach (var toolCallProperty in toolCall.EnumerateObject())
            {
                if (toolCallProperty.NameEquals(TypeName))
                {
                    hasType = true;
                    WriteToolCallType(toolCallProperty, writer, ref changed);
                    continue;
                }

                writer.WritePropertyName(toolCallProperty.Name);
                WriteElement(toolCallProperty.Value, writer, ref changed);
            }

            if (!hasType)
            {
                writer.WriteString(TypeName, "function");
                changed = true;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteToolCallType(JsonProperty property, Utf8JsonWriter writer, ref bool changed)
    {
        if (IsNonEmptyString(property.Value))
        {
            writer.WritePropertyName(TypeName);
            writer.WriteStringValue(property.Value.GetString());
            return;
        }

        writer.WriteString(TypeName, "function");
        changed = true;
    }
}
