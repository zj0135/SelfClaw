namespace SelfClaw.Infrastructure.Data.Sqlite.Models;

internal sealed record ConversationInputStateRow(bool Paused, string? PauseReason, long QueueRevision, int UnprocessedCount);
