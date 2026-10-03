namespace SelfClaw.Infrastructure.Data.Sqlite.Models;

internal sealed record ConversationInputPayload(int Version, string Prompt, string? RequestPrompt, string? RequestFingerprint);
