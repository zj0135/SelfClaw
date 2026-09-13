using System.Diagnostics;
using System.ComponentModel;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal static class WorkspaceProcess
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
