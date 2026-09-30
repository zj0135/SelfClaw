namespace SelfClaw.Core.Models;

/// <summary>Where a turn's recorded cost came from.</summary>
public enum TurnUsageCostSource
{
    None = 0,
    Estimated = 1,
    ProviderReported = 2
}
