using System.Security.Cryptography;
using System.Text;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// Deterministic message identity for an accepted input. The id is derived from the durable claim
/// and input ids so a repeated read of the same claim always yields the same user message id,
/// without persisting an unconsumed mapping.
/// </summary>
public static class ConversationInputMessageId
{
    public static Guid Compute(Guid claimId, Guid inputId)
    {
        if (claimId == Guid.Empty) throw new ArgumentException("A claim id is required.", nameof(claimId));
        if (inputId == Guid.Empty) throw new ArgumentException("An input id is required.", nameof(inputId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SelfClaw.InputMessage.v1/{claimId:D}/{inputId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
