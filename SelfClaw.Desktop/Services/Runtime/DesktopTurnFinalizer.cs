using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

public sealed class DesktopTurnFinalizer
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(5);
    private readonly ITurnFinalizationRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DesktopTurnFinalizer> _logger;

    public DesktopTurnFinalizer(
        ITurnFinalizationRepository repository,
        ILogger<DesktopTurnFinalizer> logger)
        : this(repository, TimeProvider.System, logger)
    {
    }

    internal DesktopTurnFinalizer(
        ITurnFinalizationRepository repository,
        TimeProvider timeProvider,
        ILogger<DesktopTurnFinalizer> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task<TurnFinalization?> FinalizeAsync(DesktopTurnFinalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();
        var finalization = new TurnFinalization(
            BuildAssistantMessage(request, now),
            BuildToolExecutions(request, now));
        using var cancellation = new CancellationTokenSource(PersistenceTimeout);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var written = await _repository
                    .TryFinalizeTurnAsync(finalization, cancellation.Token);
                if (!written)
                {
                    return null;
                }

                return finalization;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Timed out while persisting terminal state for turn {TurnId}.",
                    request.AssistantMessage.Id);
                throw;
            }
            catch (Exception exception) when (attempt == 1)
            {
                _logger.LogWarning(
                    exception,
                    "Retrying terminal-state persistence for turn {TurnId}.",
                    request.AssistantMessage.Id);
            }
        }

        throw new InvalidOperationException("Turn finalization retry ended without a result.");
    }

    private static MessageRecord BuildAssistantMessage(
        DesktopTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var finalMarkdown = request.FinalText is null
            ? request.AssistantMessage.MarkdownContent
            : AssistantMessageSegmenter.MergeFinalMarkdown(
                request.FinalText,
                request.AssistantMessage.MarkdownContent);

        return request.AssistantMessage with
        {
            MarkdownContent = finalMarkdown,
            Status = request.Kind switch
            {
                TurnFinalizationKind.Succeeded => MessageStatus.Completed,
                TurnFinalizationKind.Failed => MessageStatus.Failed,
                TurnFinalizationKind.Cancelled => MessageStatus.Cancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported turn outcome.")
            },
            InputTokens = request.InputTokens,
            OutputTokens = request.OutputTokens,
            DurationMs = (now - request.StartedAtUtc).TotalMilliseconds,
            ErrorMessage = request.ErrorMessage,
            UpdatedAtUtc = now
        };
    }

    private static IReadOnlyList<ToolExecutionRecord> BuildToolExecutions(
        DesktopTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var pendingStatus = request.Kind == TurnFinalizationKind.Cancelled
            ? ToolExecutionStatus.Cancelled
            : ToolExecutionStatus.Failed;
        var pendingSummary = request.Kind == TurnFinalizationKind.Cancelled
            ? "Generation stopped."
            : "The agent run ended before this tool call completed.";

        return request.ToolExecutions
            .Select(toolExecution => toolExecution.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval
                ? toolExecution with
                {
                    Status = pendingStatus,
                    ResultSummary = toolExecution.ResultSummary ?? pendingSummary,
                    DurationMs = (now - toolExecution.CreatedAtUtc).TotalMilliseconds,
                    UpdatedAtUtc = now
                }
                : toolExecution)
            .ToArray();
    }
}
