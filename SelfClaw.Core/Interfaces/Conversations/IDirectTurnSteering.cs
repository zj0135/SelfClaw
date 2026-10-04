namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The Direct loop's steering face for one run. The input session owns the durable steer claim
/// and the execution permit; the tool invoker and the approval wait only ask it whether a call
/// may still start. A granted permit is irrevocable, so an already Executing call is never
/// promised a revocation.
/// </summary>
public interface IDirectTurnSteering
{
    /// <summary>The published reason steering is unavailable this turn, or null while available.</summary>
    string? SteerBlockReason { get; }

    /// <summary>True once a steer batch is durably claimed and still waiting for its boundary.</summary>
    bool HasSupersedingSteer { get; }

    /// <summary>
    /// Grants or refuses the single execution permit. Returning false means a steer batch was
    /// just claimed, so the call must terminate with a model-visible SupersededBySteer result.
    /// </summary>
    Task<bool> TryBeginToolExecutionAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes when a steer is accepted for this run, so a waiting approval can re-check.</summary>
    Task WaitForSteerPulseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes whether this turn may consume steers. A blocked reason converts any steer that
    /// was accepted during initialization to a follow-up before the loop starts.
    /// </summary>
    Task SetSteerPolicyAsync(string? blockReason, CancellationToken cancellationToken = default);
}
