namespace SelfClaw.Core.Runtime;

public sealed record HookFailureNotice(HookSource Source, string Kind, string Message);
