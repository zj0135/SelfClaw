namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpHookRequest(
    int Sequence,
    HttpRequestMessage Request,
    string? Body,
    bool BodyTruncated);
