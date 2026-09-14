using System.Text;

namespace SelfClaw.Desktop.Services.Terminal;

internal sealed class TerminalOutputBuffer
{
    internal const int Capacity = 262144;
    private readonly object _gate = new();
    private readonly LinkedList<string> _chunks = new();
    private object? _source;
    private int _length;
    private bool _truncated;

    internal int Length { get { lock (_gate) return _length; } }

    internal void Reset(object? source)
    {
        lock (_gate) { _source = source; _chunks.Clear(); _length = 0; _truncated = false; }
    }

    internal void Append(object? source, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (!ReferenceEquals(source, _source)) return;
            if (text.Length > Capacity)
            {
                text = text[^Capacity..];
                if (char.IsLowSurrogate(text[0])) text = text[1..];
                _truncated = true;
            }
            while (_length + text.Length > Capacity && _chunks.First is { } discarded)
            {
                _length -= discarded.Value.Length;
                _chunks.RemoveFirst();
                _truncated = true;
            }
            _chunks.AddLast(text);
            _length += text.Length;
        }
    }

    internal string Drain()
    {
        lock (_gate)
        {
            var result = new StringBuilder();
            if (_truncated) result.Append("\r\n[Earlier terminal output was discarded while the view was behind.]\r\n");
            _truncated = false;
            while (_chunks.First is { } first && result.Length < 32768)
            {
                var count = Math.Min(first.Value.Length, 32768 - result.Length);
                if (count < first.Value.Length && count > 0 && char.IsHighSurrogate(first.Value[count - 1])) count--;
                if (count == 0) break;
                result.Append(first.Value.AsSpan(0, count));
                _length -= count;
                if (count == first.Value.Length) _chunks.RemoveFirst();
                else first.Value = first.Value[count..];
            }
            return result.ToString();
        }
    }
}
