namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>Either a runnable follow-up or the reason code its frozen snapshot can no longer run.</summary>
internal sealed record ConversationInputPreparation(PreparedFollowUp? FollowUp, string? Reason);
