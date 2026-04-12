using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services;

public interface IDesktopChannelAdapter
{
    DesktopChannelDescriptor Descriptor { get; }

    DesktopChannelConfiguration NormalizeConfiguration(DesktopChannelConfiguration? configuration);

    bool IsConfigured(DesktopChannelConfiguration configuration);

    void ValidateConfiguration(DesktopChannelConfiguration configuration);

    IReadOnlyList<TranscriptChannelSummaryItem> BuildSummaryItems(
        DesktopChannelConfiguration configuration,
        ProviderProfile? profile);

    string BuildConversationTitle(
        DesktopChannelConfiguration configuration,
        DesktopChannelIncomingMessage message);

    string BuildUserMessageMarkdown(DesktopChannelIncomingMessage message);

    Task<IDesktopChannelConnection> CreateConnectionAsync(
        DesktopChannelAdapterContext context,
        DesktopChannelConfiguration configuration,
        Func<DesktopChannelIncomingMessage, CancellationToken, Task> incomingMessageHandler,
        Action<DesktopChannelRuntimeState, string?> runtimeStateChanged,
        CancellationToken cancellationToken = default);
}
