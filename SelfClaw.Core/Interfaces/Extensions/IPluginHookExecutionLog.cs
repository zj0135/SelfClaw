using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces.Extensions;

/// <summary>
/// In-memory, per-plugin ring buffer of Plugin hook executions. It is diagnostic only: entries
/// never carry payloads or stdout content and are cleared when the application restarts.
/// </summary>
public interface IPluginHookExecutionLog
{
    IReadOnlyList<PluginHookExecutionEntry> GetRecent(string pluginId);
}
