namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpHookResponse(
    int Sequence,
    HttpRequestMessage Request,
    HttpResponseMessage? Response,
    TimeSpan Elapsed,
    string? Error);
