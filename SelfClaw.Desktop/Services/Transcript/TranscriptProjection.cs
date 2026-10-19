using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Transcript;

public sealed class TranscriptProjection
{
    private readonly TranscriptMessageProjector _messageProjector;
    private readonly Dictionary<Guid, (MessageRecord Message, ConversationTurnRecord Turn, bool IncludeOutcome, IReadOnlyList<ToolExecutionRecord> Tools, TranscriptRenderItem Item)> _messageCache = [];
    private readonly Dictionary<Guid, (ConversationTurnRecord Turn, TranscriptRenderItem Item)> _outcomeCache = [];
    private readonly Dictionary<Guid, (ConversationCompactionRecord Compaction, TranscriptRenderItem Item)> _compactionCache = [];
    private IReadOnlyList<ConversationRecord> _navigationSource = [];
    private IReadOnlyList<WorkspaceRoot> _workspaceSource = [];
    private IReadOnlyList<TranscriptConversationItem> _navigation = [];
    private TranscriptRenderState? _lastState;

    public TranscriptProjection(StoragePaths storagePaths)
    {
        _messageProjector = new TranscriptMessageProjector(storagePaths);
    }

    internal TranscriptRenderState? Build(TranscriptProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = BuildItems(request);
        _messageProjector.PruneToolSegmentCache(request.ToolRuns);
        UpdateNavigation(request);
        var stableItems = _lastState is { } previous && SameReferences(previous.Items, items) ? previous.Items : items;
        var state = new TranscriptRenderState(stableItems, request.AutoScroll, _navigation,
            request.SelectedConversationId?.ToString("D"), request.IsBusy, request.IsContinuation, request.ActivityText, request.AgentMode,
            request.SelectedAgentId, request.SelectedAgentName, request.CapabilityRevision, request.ToolPermissionMode,
            BuildUsageSummary(request.Turns, request.LiveUsage));
        if (state == _lastState) return null;
        _lastState = state;
        return state;
    }

    internal void Invalidate()
    {
        _lastState = null;
        _messageCache.Clear();
        _outcomeCache.Clear();
        _compactionCache.Clear();
    }

    private IReadOnlyList<TranscriptRenderItem> BuildItems(TranscriptProjectionRequest request)
    {
        var turns = request.Turns.ToDictionary(turn => turn.Id);
        var messages = request.Messages.OrderBy(message => message.Sequence).ToArray();
        var lastMessages = messages.GroupBy(message => message.TurnId).ToDictionary(group => group.Key, group => group.Last().Id);
        var lastAssistants = messages.Where(message => message.Role == MessageRole.Assistant)
            .GroupBy(message => message.TurnId).ToDictionary(group => group.Key, group => group.Last().Id);
        var toolsByMessage = IndexTools(request.ToolRuns);
        var compactions = (request.Compactions ?? [])
            .OrderBy(compaction => compaction.ThroughSequence)
            .ThenBy(compaction => compaction.CreatedAtUtc)
            .ToArray();
        var nextCompaction = 0;
        var items = new List<TranscriptRenderItem>(messages.Length + turns.Count + compactions.Length);
        long previousSequence = 0;
        foreach (var message in messages)
        {
            if (message.Sequence <= previousSequence || !turns.TryGetValue(message.TurnId, out var turn))
            {
                throw new System.IO.InvalidDataException("Transcript requires ordered unique sequences and explicit turns.");
            }

            previousSequence = message.Sequence;
            var includeOutcome = lastAssistants.GetValueOrDefault(turn.Id) == message.Id;
            IReadOnlyList<ToolExecutionRecord> tools = toolsByMessage.TryGetValue(message.Id, out var found) ? found : [];
            items.Add(BuildMessageItem(message, turn, tools, includeOutcome));
            while (nextCompaction < compactions.Length && compactions[nextCompaction].ThroughSequence <= message.Sequence)
            {
                items.Add(BuildCompactionItem(compactions[nextCompaction++]));
            }

            if (!lastAssistants.ContainsKey(turn.Id) && lastMessages[turn.Id] == message.Id)
            {
                items.Add(BuildOutcomeItem(turn));
            }
        }

        // A boundary without a loaded message (automation history) still shows its divider after the history.
        while (nextCompaction < compactions.Length) items.Add(BuildCompactionItem(compactions[nextCompaction++]));

        foreach (var turn in request.Turns.Where(turn => !lastMessages.ContainsKey(turn.Id)))
        {
            items.Add(BuildOutcomeItem(turn));
        }

        var liveMessages = messages.Select(message => message.Id).ToHashSet();
        var liveCompactions = compactions.Select(compaction => compaction.Id).ToHashSet();
        foreach (var id in _messageCache.Keys.Where(id => !liveMessages.Contains(id)).ToArray()) _messageCache.Remove(id);
        foreach (var id in _outcomeCache.Keys.Where(id => !turns.ContainsKey(id) || lastAssistants.ContainsKey(id)).ToArray()) _outcomeCache.Remove(id);
        foreach (var id in _compactionCache.Keys.Where(id => !liveCompactions.Contains(id)).ToArray()) _compactionCache.Remove(id);
        return items;
    }

    private TranscriptRenderItem BuildCompactionItem(ConversationCompactionRecord compaction)
    {
        if (_compactionCache.TryGetValue(compaction.Id, out var cached) && ReferenceEquals(cached.Compaction, compaction))
            return cached.Item;
        var item = new TranscriptRenderItem($"compression:{compaction.Id:D}", "compression", "system", "sealed", [],
            false, compaction.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            Compaction: new TranscriptCompactionNotice(compaction.Id.ToString("D"), compaction.SourceMessageCount,
                compaction.SourceTokens, compaction.SummaryTokens, compaction.Model, compaction.Summary));
        _compactionCache[compaction.Id] = (compaction, item);
        return item;
    }

    private TranscriptRenderItem BuildMessageItem(MessageRecord message, ConversationTurnRecord turn,
        IReadOnlyList<ToolExecutionRecord> tools, bool includeOutcome)
    {
        if (_messageCache.TryGetValue(message.Id, out var cached) && ReferenceEquals(cached.Message, message) &&
            ReferenceEquals(cached.Turn, turn) && cached.IncludeOutcome == includeOutcome &&
            SameReferences(cached.Tools, tools)) return cached.Item;
        var item = _messageProjector.Build(message, turn, tools, includeOutcome);
        _messageCache[message.Id] = (message, turn, includeOutcome, tools, item);
        return item;
    }

    private TranscriptRenderItem BuildOutcomeItem(ConversationTurnRecord turn)
    {
        if (_outcomeCache.TryGetValue(turn.Id, out var cached) && ReferenceEquals(cached.Turn, turn)) return cached.Item;
        var item = _messageProjector.BuildTurnOutcome(turn);
        _outcomeCache[turn.Id] = (turn, item);
        return item;
    }

    private static TranscriptUsageSummary? BuildUsageSummary(IReadOnlyList<ConversationTurnRecord> turns,
        TranscriptLiveUsage? liveUsage)
    {
        long inputTokens = 0;
        long outputTokens = 0;
        long? cachedInputTokens = null;
        int? lastRequestInputTokens = null;
        int? lastContextTokens = null;
        int? contextWindowTokens = null;
        var contextIsEstimated = false;
        var liveTurnSeen = false;
        foreach (var turn in turns.OrderBy(turn => turn.StartedAtUtc))
        {
            // The running turn's published snapshot supersedes whatever a boundary commit already wrote for
            // it: both come from the same accumulator, so adding them would count the same tokens twice.
            var isLiveTurn = liveUsage is { } live && live.TurnId == turn.Id;
            var usage = isLiveTurn ? Overlay(liveUsage!.Usage, turn.Usage) : turn.Usage;
            if (usage is null) continue;
            liveTurnSeen |= isLiveTurn;
            inputTokens += usage.InputTokens ?? 0;
            outputTokens += usage.OutputTokens ?? 0;
            // Summed like the input it is a share of, so the session hit rate stays comparable to the totals.
            if (usage.CachedInputTokens is int cachedInput) cachedInputTokens = (cachedInputTokens ?? 0) + cachedInput;
            // Context, window and last request input describe one provider call, so the latest wins
            // instead of being summed. Subagent usage is not part of the session totals.
            if (usage.LastRequestInputTokens is int requestInputTokens) lastRequestInputTokens = requestInputTokens;
            if (usage.ContextTokens is int contextTokens) lastContextTokens = contextTokens;
            if (usage.ContextWindowTokens is int windowTokens) contextWindowTokens = windowTokens;
            if (isLiveTurn) contextIsEstimated = liveUsage!.IsEstimated;
        }

        // A run can publish before its turn record reaches the snapshot; its totals still belong to the session.
        if (liveUsage is { } pending && !liveTurnSeen)
        {
            inputTokens += pending.Usage.InputTokens ?? 0;
            outputTokens += pending.Usage.OutputTokens ?? 0;
            if (pending.Usage.CachedInputTokens is int pendingCached)
                cachedInputTokens = (cachedInputTokens ?? 0) + pendingCached;
            if (pending.Usage.LastRequestInputTokens is int liveInput) lastRequestInputTokens = liveInput;
            else if (pending.Usage.ContextTokens is int liveContext) lastContextTokens = liveContext;
            if (pending.Usage.ContextWindowTokens is int liveWindow) contextWindowTokens = liveWindow;
            contextIsEstimated = pending.IsEstimated;
        }

        var context = lastRequestInputTokens ?? lastContextTokens;
        if (context is null) return null;
        return new TranscriptUsageSummary(inputTokens, outputTokens, context, contextWindowTokens, contextIsEstimated,
            cachedInputTokens);
    }

    /// <summary>
    /// The live snapshot wins field by field, because it is the same accumulator one request later; a locally
    /// counted estimate only fills the context fields, so the persisted totals survive underneath it.
    /// </summary>
    private static TurnUsage Overlay(TurnUsage live, TurnUsage? persisted)
    {
        if (persisted is not { } previous) return live;
        return new TurnUsage(
            live.Model ?? previous.Model,
            live.InputTokens ?? previous.InputTokens,
            live.UncachedInputTokens ?? previous.UncachedInputTokens,
            live.CachedInputTokens ?? previous.CachedInputTokens,
            live.CacheWriteInputTokens ?? previous.CacheWriteInputTokens,
            live.OutputTokens ?? previous.OutputTokens,
            live.ReasoningTokens ?? previous.ReasoningTokens,
            live.TotalTokens ?? previous.TotalTokens,
            Math.Max(live.ProviderCalls, previous.ProviderCalls),
            live.ContextTokens ?? previous.ContextTokens,
            live.ContextWindowTokens ?? previous.ContextWindowTokens,
            live.CostUsdMicros ?? previous.CostUsdMicros,
            live.CostSource == TurnUsageCostSource.None ? previous.CostSource : live.CostSource,
            live.AdditionalCountsJson ?? previous.AdditionalCountsJson,
            live.LastRequestInputTokens ?? previous.LastRequestInputTokens);
    }

    private void UpdateNavigation(TranscriptProjectionRequest request)
    {
        if (SameReferences(_navigationSource, request.Conversations) && SameReferences(_workspaceSource, request.WorkspaceRoots))
            return;
        _navigationSource = request.Conversations.ToArray();
        _workspaceSource = request.WorkspaceRoots.ToArray();
        var roots = _workspaceSource.ToDictionary(root => root.Id);
        _navigation = _navigationSource.OrderByDescending(conversation => conversation.UpdatedAtUtc)
            .ThenBy(conversation => conversation.CreatedAtUtc)
            .Select(conversation => BuildConversationItem(conversation, roots)).ToArray();
    }

    private static Dictionary<Guid, List<ToolExecutionRecord>> IndexTools(IReadOnlyList<ToolExecutionRecord> tools)
    {
        var result = new Dictionary<Guid, List<ToolExecutionRecord>>();
        foreach (var tool in tools)
        {
            if (tool.MessageId is not Guid messageId) continue;
            if (!result.TryGetValue(messageId, out var records)) result[messageId] = records = [];
            records.Add(tool);
        }
        return result;
    }

    private static TranscriptConversationItem BuildConversationItem(ConversationRecord conversation,
        IReadOnlyDictionary<Guid, WorkspaceRoot> roots)
    {
        var root = conversation.WorkspaceRootId is Guid id ? roots.GetValueOrDefault(id) : null;
        return new(conversation.Id.ToString("D"), conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), conversation.WorkspaceRootId?.ToString("D"),
            root?.Name, root?.RootPath, root?.GitRepositoryId?.ToString("D"), root?.GitRepositoryName, root?.GitBranchName,
            root?.IsManagedWorktree == true);
    }

    private static bool SameReferences<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : class
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!ReferenceEquals(left[index], right[index])) return false;
        return true;
    }
}
