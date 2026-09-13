namespace SelfClaw.Core.Interfaces;

/// <summary>Persists recovery evidence before a tool is allowed to execute.</summary>
public interface IToolExecutionCheckpoint
{
    Task BeforeExecutionAsync(CancellationToken cancellationToken);
}
