using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class DesktopTurnFinalizer : IRecordedTurnCommitter
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(5);
    private readonly IConversationTurnRepository _repository;
    private readonly ILogger<DesktopTurnFinalizer> _logger;

    public DesktopTurnFinalizer(
        IConversationTurnRepository repository,
        ILogger<DesktopTurnFinalizer> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public async Task<bool> TryCommitAsync(RecordedTurnCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var finalization = commit.Finalization;

        using var cancellation = new CancellationTokenSource(PersistenceTimeout);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var written = await _repository
                    .TryFinalizeTurnAsync(finalization, cancellation.Token);
                if (!written)
                {
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Canceled while persisting terminal state for turn {TurnId}.",
                    finalization.Turn.Id);
                throw;
            }
            catch (Exception exception) when (attempt == 1)
            {
                _logger.LogWarning(
                    exception,
                    "Retrying terminal-state persistence for turn {TurnId}.",
                    finalization.Turn.Id);
            }
        }

        throw new InvalidOperationException("Turn finalization retry ended without a result.");
    }
}
