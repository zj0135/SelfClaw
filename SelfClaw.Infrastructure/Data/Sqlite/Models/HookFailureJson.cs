namespace SelfClaw.Infrastructure.Data.Sqlite.Models;

internal sealed record HookFailureJson(HookSourceJson Source, string Kind, string Message);
