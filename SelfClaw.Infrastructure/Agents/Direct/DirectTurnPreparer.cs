using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// Prepares one Direct turn: it validates the request shape, resolves the provider and capability scope,
/// publishes the steer policy, builds the replayed units, runs the turn-start compaction and assembles the
/// <see cref="DirectTurnSetup"/> the loop streams. Nothing here consumes the provider stream; a setup either
/// produces the leases the runtime then owns or throws with every lease it opened already released.
/// </summary>
internal sealed class DirectTurnPreparer
{
    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IDirectTurnCapabilityResolver _capabilityResolver;
    private readonly IDirectCompressionPolicySource _compressionPolicy;
    private readonly DirectCompactionStep _compactionStep;
    private readonly IAttachmentContentReader _attachmentContentReader;
    private readonly ILogger<DirectTurnPreparer> _logger;

    internal DirectTurnPreparer(
        IAiChatClientFactory chatClientFactory,
        IDirectTurnCapabilityResolver capabilityResolver,
        IDirectCompressionPolicySource compressionPolicy,
        DirectCompactionStep compactionStep,
        IAttachmentContentReader attachmentContentReader,
        ILogger<DirectTurnPreparer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        ArgumentNullException.ThrowIfNull(compressionPolicy);
        ArgumentNullException.ThrowIfNull(compactionStep);
        ArgumentNullException.ThrowIfNull(attachmentContentReader);
        _chatClientFactory = chatClientFactory;
        _capabilityResolver = capabilityResolver;
        _compressionPolicy = compressionPolicy;
        _compactionStep = compactionStep;
        _attachmentContentReader = attachmentContentReader;
        _logger = logger ?? NullLogger<DirectTurnPreparer>.Instance;
    }

    internal async Task<DirectTurnSetup> PrepareAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        ValidateRequest(request);
        var preparation = await _chatClientFactory.PrepareAsync(request.ModelProfileId, cancellationToken)
            .ConfigureAwait(false);
        var prepared = request with { ModelProfileId = preparation.Profile.Id };
        // Resolved from the profile that is actually going to be called: the gate is per model, and the
        // toolset needs to know before it can describe or perform an image read.
        var imageCapability = ToolImageDeliveryRules.Resolve(preparation.Profile);
        var capabilityLease = await _capabilityResolver.ResolveAsync(prepared, imageCapability, cancellationToken)
            .ConfigureAwait(false);
        AiChatClientLease? providerLease = null;
        try
        {
            PublishCapabilityDiagnostics(capabilityLease, writer);
            await PublishSteerPolicyAsync(prepared, capabilityLease, cancellationToken).ConfigureAwait(false);
            var invoker = new DirectToolInvoker(prepared, capabilityLease.Bindings);
            providerLease = _chatClientFactory.Create(
                preparation,
                new AiChatClientPipelineOptions(capabilityLease.Tools));
            // Direct has no resumable session: the whole conversation is replayed into every turn, so only
            // the CLI parsers ever report a session id.
            writer.TryWrite(new RunStartedEvent(
                SessionId: null,
                providerLease.Profile.Model,
                AgentKind: null));
            return await BuildSetupAsync(prepared, capabilityLease, providerLease, invoker, imageCapability, writer,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ownership transfers to the runtime only after the whole setup succeeds.
            await DirectTurnLeaseDisposer.DisposeAsync(providerLease, capabilityLease, _logger).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateRequest(DirectChatTurnRequest request)
    {
        if ((request.InputSession is not null || request.Steering is not null) &&
            request.ExecutionContext.Origin != DirectTurnOrigin.Interactive)
        {
            throw new InvalidDataException("Only Interactive Direct turns can consume input boundaries or steer.");
        }

        if (DirectTurnPolicy.For(request).RequiresToolExecutionCheckpoint && request.ToolExecutionCheckpoint is null)
        {
            throw new InvalidDataException("A continuation requires a durable tool execution checkpoint.");
        }

        // Rejected here, before any provider connection or capability lease exists, because the batch is what
        // makes the prompt describe itself as delivering Subagent results: a turn that cannot deliver them must
        // not reach prompt assembly at all.
        if (request.ExecutionContext.CompletionBatch is not null &&
            request.ExecutionContext.Origin != DirectTurnOrigin.Continuation)
        {
            throw new InvalidDataException("Only a continuation turn can carry a Subagent completion batch.");
        }
    }

    private static void PublishCapabilityDiagnostics(
        DirectTurnCapabilityLease capabilityLease,
        ChannelWriter<AgentStreamEvent> writer)
    {
        foreach (var diagnostic in capabilityLease.Diagnostics)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
        }
    }

    // Published before the request starts: an input accepted during initialization is converted to a follow-up
    // here rather than being steered into an already-active skill turn.
    private static async Task PublishSteerPolicyAsync(
        DirectChatTurnRequest request,
        DirectTurnCapabilityLease capabilityLease,
        CancellationToken cancellationToken)
    {
        if (request.Steering is null)
        {
            return;
        }

        var steerBlockReason = capabilityLease.ExplicitSkillIds.Count > 0
            ? ConversationInputReason.SkillRequiresNewTurn
            : null;
        await request.Steering.SetSteerPolicyAsync(steerBlockReason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DirectTurnSetup> BuildSetupAsync(
        DirectChatTurnRequest request,
        DirectTurnCapabilityLease capabilityLease,
        AiChatClientLease providerLease,
        DirectToolInvoker invoker,
        ToolImageCapability imageCapability,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var budget = new DirectPromptBudget(
            AiChatOptions.ResolveContextWindowTokens(providerLease.Profile),
            providerLease.Options.MaxOutputTokens);
        // The units are built once and shared by counting, compaction planning and the prompt itself:
        // rebuilding them per request would rescan the whole stored history on every turn. Images are
        // read before the units so counting, planning and the request measure the same bytes.
        var attachmentContents = await _attachmentContentReader.ReadAsync(request.Messages, cancellationToken)
            .ConfigureAwait(false);
        // A request without tool executions carries no tool history; normalizing once keeps the units and
        // the plan reading the same list.
        var toolExecutions = request.ToolExecutions ?? [];
        var units = DirectHistoryReplay.BuildUnits(
            request.Messages,
            request.Turns,
            toolExecutions,
            capabilityLease.MessageAdjustments,
            attachmentContents);
        // The plan is read from the stored history before compaction runs, so what it decides is independent
        // of the units this turn happens to replay.
        var plan = TodoPlanHistory.FindLatest(request.Messages, toolExecutions);
        var policy = await _compressionPolicy.GetAsync(cancellationToken).ConfigureAwait(false);
        var compaction = await _compactionStep.CompactTurnStartAsync(
                new DirectTurnCompaction(request, capabilityLease, providerLease, budget, policy, units, plan),
                writer,
                cancellationToken)
            .ConfigureAwait(false);
        DirectCompactionStep.EnsureTurnStartPromptFitsWindow(budget, compaction);
        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

        // The section is the one compaction already counted, so the request that is sized and the request
        // that is sent can never disagree about it.
        var build = DirectHistoryReplay.Compose(
            units,
            DirectSystemPrompt.BuildSystemText(
                request.Agent.Instructions,
                capabilityLease.SystemInstructions,
                request.ExecutionContext,
                compaction.Compression,
                compaction.TodoSection),
            DirectSystemPrompt.BuildTrailingProtectedMessages(request.ExecutionContext),
            compaction.Compression);
        return new DirectTurnSetup(
            capabilityLease,
            providerLease,
            invoker,
            build.SystemText,
            build.Units,
            build.TrailingProtectedMessages,
            policy,
            budget,
            request.ConversationId,
            request.TurnId,
            request.InputSession,
            imageCapability.Delivery,
            compaction.TodoSection);
    }
}
