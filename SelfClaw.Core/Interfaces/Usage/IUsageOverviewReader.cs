using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Read-only application-wide usage aggregation for the About dashboard. Deliberately separate from
/// <see cref="IUsageStatisticsReader"/>, which answers per-conversation questions: mixing a global
/// time window into that contract would force conversation details to carry parameters they ignore.
/// </summary>
public interface IUsageOverviewReader
{
    Task<UsageOverview> ReadAsync(UsageOverviewQuery query, CancellationToken cancellationToken = default);
}
