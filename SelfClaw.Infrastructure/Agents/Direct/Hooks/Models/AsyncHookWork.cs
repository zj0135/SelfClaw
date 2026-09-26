using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record AsyncHookWork(
    ResolvedPluginHook Hook,
    string EventName,
    ReadOnlyMemory<byte> Payload,
    Guid TurnId,
    HookProcessStart Start);
