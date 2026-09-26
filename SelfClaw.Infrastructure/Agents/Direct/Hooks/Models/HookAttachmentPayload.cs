namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HookAttachmentPayload(string FileName, string MediaType, long ByteLength);
