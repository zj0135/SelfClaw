using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// In-process Direct runtime. It translates Microsoft.Extensions.AI streaming
/// content into the same provider-neutral event stream consumed by the desktop transcript.
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

    private static readonly string ToolLoopExhaustedMessage =
        $"BudgetExhausted: the turn reached its {DirectConversationLoop.MaximumProviderRequestsPerTurn} provider request limit before it could continue.";

    /// <summary>
    /// Reported when the output-token cap is hit before any text is produced. There is no
    /// partial answer to resume from, so the limit is likely configured too low to be usable.
    /// </summary>
    private const string TruncatedWithoutOutputMessage =
        "The response reached the configured output-token limit without producing any output. " +
        "Raise the output-token limit for this model and try again.";

    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IDirectTurnCapabilityResolver _capabilityResolver;
    private readonly DirectPromptComposer _promptComposer;
    private readonly ILogger<DirectAgentChatRuntime> _logger;

    public DirectAgentChatRuntime(
        IAiChatClientFactory chatClientFactory,
        IDirectTurnCapabilityResolver capabilityResolver,
        DirectPromptComposer promptComposer,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _capabilityResolver = capabilityResolver;
        _promptComposer = promptComposer;
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
            setup = await SetupTurnAsync(request, writer, cancellationToken).ConfigureAwait(false);
            var ready = (DirectTurnSetup.Ready)setup;
            output.ConfigureUsage(
                ready.ProviderLease.Profile.Configuration,
                ready.ProviderLease.Profile.Model,
                AiChatOptions.ResolveContextWindowTokens(ready.ProviderLease.Profile));
            var outcome = await new DirectConversationLoop().RunAsync(ready, output, cancellationToken).ConfigureAwait(false);
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
            var ready = setup as DirectTurnSetup.Ready;
            if (setup is not null)
            {
                await DisposeResourcesAsync(ready?.ProviderLease, setup.CapabilityLease).ConfigureAwait(false);
            }

            // Every path above writes exactly one terminal; the dispatcher synthesizes one if a future
            // path ever misses, so this producer does not keep a second copy of that guarantee.
            writer.TryComplete();
        }
    }

    private async Task<DirectTurnSetup> SetupTurnAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        if ((request.InputSession is not null || request.Steering is not null) &&
            request.ExecutionContext.Origin != DirectTurnOrigin.Interactive)
            throw new InvalidDataException("Only Interactive Direct turns can consume input boundaries or steer.");

        if (DirectTurnPolicy.For(request).RequiresToolExecutionCheckpoint && request.ToolExecutionCheckpoint is null)
        {
            throw new InvalidDataException("A continuation requires a durable tool execution checkpoint.");
        }

        var preparation = await _chatClientFactory.PrepareAsync(request.ModelProfileId, cancellationToken).ConfigureAwait(false);
        request = request with { ModelProfileId = preparation.Profile.Id };
        var capabilityLease = await _capabilityResolver.ResolveAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AiChatClientLease? providerLease = null;
        try
        {
            foreach (var diagnostic in capabilityLease.Diagnostics)
            {
                writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
            }

            // Publish the steer policy before the request starts: an input accepted during initialization
            // is converted to a follow-up here rather than being steered into an already-active skill turn.
            if (request.Steering is not null)
            {
                var steerBlockReason = capabilityLease.ExplicitSkillIds.Count > 0
                    ? ConversationInputReason.SkillRequiresNewTurn
                    : null;
                await request.Steering.SetSteerPolicyAsync(steerBlockReason, cancellationToken).ConfigureAwait(false);
            }

            var invoker = new DirectToolInvoker(request, capabilityLease.Bindings);
            providerLease = _chatClientFactory.Create(
                preparation,
                new AiChatClientPipelineOptions(capabilityLease.Tools));

            // Direct has no resumable session: the whole conversation is replayed into every turn, so only
            // the CLI parsers ever report a session id.
            writer.TryWrite(new RunStartedEvent(
                SessionId: null,
                providerLease.Profile.Model,
                AgentKind: null));
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

            var messages = _promptComposer.BuildMessages(
                request.Messages,
                request.Turns,
                request.ToolExecutions ?? [],
                request.Agent.Instructions,
                capabilityLease.SystemInstructions,
                capabilityLease.MessageAdjustments,
                request.ExecutionContext,
                new DirectPromptBudget(
                    AiChatOptions.ResolveContextWindowTokens(providerLease.Profile),
                    providerLease.Options.MaxOutputTokens),
                providerLease.Options.Tools);
            return new DirectTurnSetup.Ready(capabilityLease, providerLease, invoker, messages, request.InputSession);
        }
        catch
        {
            // Ownership transfers to the producer only after the whole setup succeeds.
            await DisposeResourcesAsync(providerLease, capabilityLease).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Emits the terminal RunCompleted event for the observed finish reason. A length stop with
    /// partial text is reported as Truncated (the partial answer is valid and kept in the prompt
    /// history, so the model can resume from it if the user continues); a length stop without text
    /// and an exhausted tool loop are both reported as Failed.
    /// </summary>
    private RunCompletedEvent WriteTerminalOutcome(
        ChannelWriter<AgentStreamEvent> writer,
        DirectLoopOutcome outcome,
        DirectEventTranslator output)
    {
        if (outcome == DirectLoopOutcome.ContentFiltered)
        {
            var blocked = new RunCompletedEvent(RunCompletionStatus.Blocked, output.FinalTextOrNull,
                "The provider filtered or refused the response (ContentFilter).");
            writer.TryWrite(blocked);
            return blocked;
        }

        if (outcome == DirectLoopOutcome.Length && output.HasFinalText)
        {
            _logger.LogInformation(
                "Direct AI agent turn stopped at the output-token cap; reporting it as truncated.");
            var truncated = new RunCompletedEvent(
                RunCompletionStatus.Truncated,
                output.FinalText,
                TruncatedMessage);
            writer.TryWrite(truncated);
            return truncated;
        }

        if (outcome == DirectLoopOutcome.Length)
        {
            _logger.LogWarning(
                "Direct AI agent turn hit the output-token cap without producing any text.");
            var failed = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                ErrorMessage: TruncatedWithoutOutputMessage,
                FinalText: null);
            writer.TryWrite(failed);
            return failed;
        }

        if (outcome == DirectLoopOutcome.ContextBudgetExceeded)
        {
            _logger.LogWarning("Direct AI agent turn could not fit a steer batch into the model context window.");
            var failed = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                "ContextBudgetExceeded: the queued steer could not fit in the model context window; the unconsumed input was held.");
            writer.TryWrite(failed);
            return failed;
        }

        if (outcome == DirectLoopOutcome.BudgetExhausted)
        {
            _logger.LogWarning("Direct AI agent turn exhausted its {RequestLimit} provider request budget.",
                DirectConversationLoop.MaximumProviderRequestsPerTurn);
            var failed = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                ToolLoopExhaustedMessage);
            writer.TryWrite(failed);
            return failed;
        }

        var succeeded = new RunCompletedEvent(
            RunCompletionStatus.Succeeded,
            output.FinalText,
            ErrorMessage: null);
        writer.TryWrite(succeeded);
        return succeeded;
    }

    private async Task DisposeResourcesAsync(
        AiChatClientLease? providerLease,
        DirectTurnCapabilityLease capabilityLease)
    {
        try
        {
            providerLease?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
        }

        try
        {
            await capabilityLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct turn capability lease.");
        }
    }
}
