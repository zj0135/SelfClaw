using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Read-only usage statistics. Interactive-conversation usage and Subagent usage are reported
/// separately: a parent turn never includes the consumption of the Subagent tasks it delegated.
/// </summary>
public interface IUsageStatisticsReader
{
    Task<UsageReport> GetConversationUsageAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<UsageReport> GetSubagentUsageAsync(
        Guid parentConversationId,
        CancellationToken cancellationToken = default);
}
