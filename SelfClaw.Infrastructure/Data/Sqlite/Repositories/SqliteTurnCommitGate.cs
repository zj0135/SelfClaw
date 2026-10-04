namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

/// <summary>
/// Test-only fault injection for turn commits. Production composition leaves it null.
/// </summary>
internal sealed class SqliteTurnCommitGate
{
    public Func<string, CancellationToken, Task>? BeforeStart { get; set; }

    public Func<string, CancellationToken, Task>? BeforeFinalize { get; set; }
}
