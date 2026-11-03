using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// In-process Direct runtime: it owns the turn's channel, the producer that drives preparation and the explicit
/// loop, the terminal event and the lease disposal. Provider payload translation lives in
/// <see cref="DirectEventTranslator"/> and everything a turn must be set up with before it streams lives in
/// <see cref="DirectTurnPreparer"/>, so this class only decides how a turn starts, ends and releases.
/// </summary>
internal sealed class DirectAgentChatRuntime : IAgentRuntimeAdapter
{
    /// <summary>
    /// Reported when the model stops at its output-token cap. Hitting the cap is normal
    /// once the limit is configured, so this is informational rather than an error.
    /// </summary>
    private const string TruncatedMessage =
        "The response reached the configured output-token limit. Continue the message to " +
        "have the model resume from where it stopped.";

    /// <summary>
    /// Reported when the output-token cap is hit before any text is produced. There is no
    /// partial answer to resume from, so the limit is likely configured too low to be usable.
    /// </summary>
    private const string TruncatedWithoutOutputMessage =
        "The response reached the configured output-token limit without producing any output. " +
        "Raise the output-token limit for this model and try again.";

    private readonly DirectTurnPreparer _turnPreparer;
    private readonly DirectTurnRunFactory _turnRuns;
    private readonly ILogger<DirectAgentChatRuntime> _logger;

    public DirectAgentChatRuntime(
        DirectTurnPreparer turnPreparer,
        DirectTurnRunFactory turnRuns,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(turnPreparer);
        ArgumentNullException.ThrowIfNull(turnRuns);
        _turnPreparer = turnPreparer;
        _turnRuns = turnRuns;
        _logger = logger ?? NullLogger<DirectAgentChatRuntime>.Instance;
    }

    public AgentExecutionMode Mode => AgentExecutionMode.Direct;

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The dispatcher routes by Mode, so a Direct turn always arrives as a DirectChatTurnRequest.
        var directRequest = request as DirectChatTurnRequest
            ?? throw new ArgumentException(
                $"The Direct runtime requires a {nameof(DirectChatTurnRequest)}.", nameof(request));
        return StreamCoreAsync(directRequest, cancellationToken);
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        DirectChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        // The linked source lets an abandoned enumerator (consumer stops reading without
        // cancelling) still tear down the provider stream instead of orphaning it.
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceEventsAsync(request, channel.Writer, producerCancellation.Token);

        try
        {
            await foreach (var streamEvent in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceEventsAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var output = new DirectEventTranslator(writer);
        DirectTurnSetup? setup = null;
        try
        {
            setup = await _turnPreparer.PrepareAsync(request, writer, cancellationToken).ConfigureAwait(false);
            output.ConfigureUsage(
                setup.ProviderLease.Profile.Configuration,
                setup.ProviderLease.Profile.Model,
                AiChatOptions.ResolveContextWindowTokens(setup.ProviderLease.Profile));
            var outcome = await _turnRuns.Create(setup, output).RunAsync(cancellationToken).ConfigureAwait(false);
            output.ReportUsage();
            WriteTerminalOutcome(writer, outcome, output);
        }
        catch (OperationCanceledException exception)
        {
            output.ReportUsage();
            writer.TryComplete(exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Direct AI agent turn failed.");
            output.ReportUsage();
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                exception.Message));
        }
        finally
        {
            if (setup is not null)
            {
                await DirectTurnLeaseDisposer.DisposeAsync(setup.ProviderLease, setup.CapabilityLease, _logger)
                    .ConfigureAwait(false);
            }

            // Every path above writes exactly one terminal; the dispatcher synthesizes one if a future
            // path ever misses, so this producer does not keep a second copy of that guarantee.
            writer.TryComplete();
        }
    }

    private void WriteTerminalOutcome(
        ChannelWriter<AgentStreamEvent> writer,
        DirectLoopOutcome outcome,
        DirectEventTranslator output)
    {
        LogStop(outcome, output.HasFinalText);
        var terminal = ResolveTerminal(outcome, output);
        writer.TryWrite(new RunCompletedEvent(terminal.Status, terminal.FinalText, terminal.Message));
    }

    /// <summary>
    /// The terminal shape of the observed finish reason. A length stop with partial text is Truncated: the
    /// partial answer is valid and kept in the prompt history, so the model can resume from it if the user
    /// continues. A length stop without text and a stop the loop had to impose are Failed.
    /// </summary>
    private static (RunCompletionStatus Status, string? FinalText, string? Message) ResolveTerminal(
        DirectLoopOutcome outcome,
        DirectEventTranslator output)
        => outcome switch
        {
            DirectLoopOutcome.ContentFiltered => (
                RunCompletionStatus.Blocked,
                output.FinalTextOrNull,
                "The provider filtered or refused the response (ContentFilter)."),
            DirectLoopOutcome.Length when output.HasFinalText => (
                RunCompletionStatus.Truncated,
                output.FinalText,
                TruncatedMessage),
            DirectLoopOutcome.Length => (
                RunCompletionStatus.Failed,
                null,
                TruncatedWithoutOutputMessage),
            DirectLoopOutcome.ContextBudgetExceeded => (
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                DirectLoopMessages.ContextBudgetExceededTerminal),
            DirectLoopOutcome.BudgetExhausted => (
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                DirectLoopMessages.BudgetExhaustedTerminal),
            DirectLoopOutcome.Stalled => (
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                DirectLoopMessages.NoProgressTerminal),
            _ => (RunCompletionStatus.Succeeded, output.FinalText, null)
        };

    // Logged before the event is written, so a consumer that is already closed never hides why the turn stopped.
    private void LogStop(DirectLoopOutcome outcome, bool hasFinalText)
    {
        switch (outcome)
        {
            case DirectLoopOutcome.Length when hasFinalText:
                _logger.LogInformation(
                    "Direct AI agent turn stopped at the output-token cap; reporting it as truncated.");
                break;
            case DirectLoopOutcome.Length:
                _logger.LogWarning(
                    "Direct AI agent turn hit the output-token cap without producing any text.");
                break;
            case DirectLoopOutcome.ContextBudgetExceeded:
                _logger.LogWarning(
                    "Direct AI agent turn could not fit a steer batch into the model context window.");
                break;
            case DirectLoopOutcome.BudgetExhausted:
                _logger.LogWarning("Direct AI agent turn exhausted its {RequestLimit} provider request budget.",
                    DirectTurnRun.MaximumProviderRequestsPerTurn);
                break;
            case DirectLoopOutcome.Stalled:
                _logger.LogWarning(
                    "Direct AI agent turn repeated the same tool batch {RepeatLimit} times and was stopped as stalled.",
                    NoProgressGuard.RepeatedBatchLimit);
                break;
        }
    }
}
