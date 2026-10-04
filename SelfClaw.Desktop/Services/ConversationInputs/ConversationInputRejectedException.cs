namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>A queue operation refused for a known <see cref="SelfClaw.Core.Models.ConversationInputReason"/>.</summary>
internal sealed class ConversationInputRejectedException(string reason) : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}
