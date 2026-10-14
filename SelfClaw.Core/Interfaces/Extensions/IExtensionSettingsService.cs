using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IExtensionSettingsService
{
    Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<ExtensionPackageView> ImportPackageAsync(
        ExtensionKind kind,
        string selectedPath,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        ExtensionItemKey key,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task AcknowledgePluginPermissionsAsync(
        string id,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(ExtensionItemKey key, CancellationToken cancellationToken = default);

    Task<McpServerView> SaveMcpServerAsync(
        SaveMcpServerCommand command,
        CancellationToken cancellationToken = default);

    Task<McpHealthResult> TestMcpServerAsync(string id, CancellationToken cancellationToken = default);
}
