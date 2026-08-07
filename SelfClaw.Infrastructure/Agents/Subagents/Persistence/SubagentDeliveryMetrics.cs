using System.Diagnostics.Metrics;

namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence;

internal static class SubagentDeliveryMetrics
{
    private static readonly Meter Meter = new("SelfClaw.Subagents", "1.0.0");
    private static readonly Counter<long> LeaseBatches = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.lease_batches");
    private static readonly Counter<long> LeasedDeliveries = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.leased");
    private static readonly Counter<long> DeliveredDeliveries = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.delivered");
    private static readonly Counter<long> RetriedDeliveries = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.retried");
    private static readonly Counter<long> DeadLetteredDeliveries = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.dead_lettered");
    private static readonly Counter<long> RecoveredDeliveries = Meter.CreateCounter<long>(
        "selfclaw.subagent.delivery.recovered");

    internal static void RecordLease(int count)
    {
        LeaseBatches.Add(1);
        LeasedDeliveries.Add(count);
    }

    internal static void RecordResolution(
        int delivered,
        int retried,
        int deadLettered)
    {
        DeliveredDeliveries.Add(delivered);
        RetriedDeliveries.Add(retried);
        DeadLetteredDeliveries.Add(deadLettered);
    }

    internal static void RecordRecovery(int count)
        => RecoveredDeliveries.Add(count);
}
