using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Transcript.Views;

/// <summary>
/// The running turn's usage as its runtime published it, for display only. It replaces that turn's
/// persisted usage instead of adding to it: a boundary commit may already have written an earlier snapshot
/// of the same accumulator, so summing both would count the same tokens twice.
/// </summary>
/// <param name="IsEstimated">
/// The snapshot's context size was counted locally and no provider has confirmed it yet, so the meter shows
/// it as approximate. It never reaches the usage table.
/// </param>
public sealed record TranscriptLiveUsage(Guid TurnId, TurnUsage Usage, bool IsEstimated);
