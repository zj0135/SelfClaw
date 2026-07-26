using System.Text;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class BoundedDiagnosticBuffer
{
    internal const int DefaultMaximumCharacters = 64 * 1024;

    private readonly int _maximumCharacters;
    private readonly StringBuilder _content = new();
    private readonly object _sync = new();

    public BoundedDiagnosticBuffer(int maximumCharacters = DefaultMaximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        _maximumCharacters = maximumCharacters;
    }

    public void Append(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_sync)
        {
            if (_content.Length >= _maximumCharacters)
            {
                return;
            }

            var remaining = _maximumCharacters - _content.Length;
            var separatorLength = _content.Length == 0 ? 0 : Environment.NewLine.Length;
            if (separatorLength >= remaining)
            {
                return;
            }

            if (separatorLength > 0)
            {
                _content.Append(Environment.NewLine);
                remaining -= separatorLength;
            }

            _content.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
        }
    }

    public string? Read()
    {
        lock (_sync)
        {
            return _content.Length == 0 ? null : _content.ToString();
        }
    }
}
