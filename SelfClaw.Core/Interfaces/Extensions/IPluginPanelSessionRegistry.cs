namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Lets the extension settings module close a Plugin's open panels before it drains that Plugin's
/// version directories. Without it, disabling or deleting a Plugin whose panel is open would block on a
/// version lease that only the UI can release, and the settings mutation would hang forever.
/// </summary>
public interface IPluginPanelSessionRegistry
{
    Task CloseAsync(string pluginId, CancellationToken cancellationToken = default);
}
