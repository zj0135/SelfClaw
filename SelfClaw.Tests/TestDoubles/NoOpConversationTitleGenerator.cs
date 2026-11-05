using SelfClaw.Core.Interfaces;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>A title generator that never produces a title, for tests that do not exercise titling.</summary>
internal sealed class NoOpConversationTitleGenerator : IConversationTitleGenerator
{
    public Task<string?> GenerateTitleAsync(string prompt, Guid? modelProfileId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
