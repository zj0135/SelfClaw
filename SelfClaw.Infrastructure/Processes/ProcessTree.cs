using System.ComponentModel;
using System.Diagnostics;

namespace SelfClaw.Infrastructure.Processes;

internal static class ProcessTree
{
    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
