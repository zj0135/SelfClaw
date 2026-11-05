namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Produces a conversation title from its first user message, using the conversation's own model.
/// Failures are the implementation's problem: a null title means "keep the current one".
/// </summary>
public interface IConversationTitleGenerator
{
    Task<string?> GenerateTitleAsync(string prompt, Guid? modelProfileId, CancellationToken cancellationToken = default);
}
