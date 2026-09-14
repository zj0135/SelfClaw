using System.IO;
using System.Text;

namespace SelfClaw.Desktop.Services.Terminal;

internal sealed class TerminalOutputReader
{
    public async Task ReadAsync(Stream stream, Action<string> publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(publish);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            publish(new string(buffer, 0, read));
    }
}
