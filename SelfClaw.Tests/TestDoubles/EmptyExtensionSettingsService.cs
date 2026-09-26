using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyExtensionSettingsService : IExtensionSettingsService
{
    public Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSettingsState(0, null, [], [], [], [], []));
    public Task<ExtensionPackageView> ImportPackageAsync(ExtensionKind kind, string selectedPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ExtensionPackageView> ImportPluginFolderAsync(string folderPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PluginReloadResult> ReloadPluginAsync(string pluginId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetEnabledAsync(ExtensionItemKey key, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task AcknowledgePluginPermissionsAsync(string id, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(ExtensionItemKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<McpServerView> SaveMcpServerAsync(SaveMcpServerCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<McpHealthResult> TestMcpServerAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
