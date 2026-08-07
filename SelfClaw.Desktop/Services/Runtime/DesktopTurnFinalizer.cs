using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class DesktopTurnFinalizer : IRecordedTurnCommitter
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(5);
    private readonly ITurnFinalizationRepository _repository;
    private readonly ILogger<DesktopTurnFinalizer> _logger;

    public DesktopTurnFinalizer(
        ITurnFinalizationRepository repository,
        ILogger<DesktopTurnFinalizer> logger)
    {
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
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Timed out while persisting terminal state for turn {TurnId}.",
                    finalization.AssistantMessage.Id);
                throw;
            }
            catch (Exception exception) when (attempt == 1)
            {
                _logger.LogWarning(
                    exception,
                    "Retrying terminal-state persistence for turn {TurnId}.",
                    finalization.AssistantMessage.Id);
            }
        }

        throw new InvalidOperationException("Turn finalization retry ended without a result.");
    }
}
