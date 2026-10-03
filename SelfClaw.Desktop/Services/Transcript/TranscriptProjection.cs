using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Transcript;

public sealed class TranscriptProjection
{
    private readonly TranscriptMessageProjector _messageProjector;
    private readonly Dictionary<Guid, (MessageRecord Message, ConversationTurnRecord Turn, bool IncludeOutcome, IReadOnlyList<ToolExecutionRecord> Tools, TranscriptRenderItem Item)> _messageCache = [];
    private readonly Dictionary<Guid, (ConversationTurnRecord Turn, TranscriptRenderItem Item)> _outcomeCache = [];
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
            request.SelectedAgentId, request.SelectedAgentName, request.CapabilityRevision, request.ToolPermissionMode);
        if (state == _lastState) return null;
        _lastState = state;
        return state;
    }

    internal void Invalidate()
    {
        _lastState = null;
        _messageCache.Clear();
        _outcomeCache.Clear();
    }

    private IReadOnlyList<TranscriptRenderItem> BuildItems(TranscriptProjectionRequest request)
    {
        var turns = request.Turns.ToDictionary(turn => turn.Id);
        var messages = request.Messages.OrderBy(message => message.Sequence).ToArray();
        var lastMessages = messages.GroupBy(message => message.TurnId).ToDictionary(group => group.Key, group => group.Last().Id);
        var lastAssistants = messages.Where(message => message.Role == MessageRole.Assistant)
            .GroupBy(message => message.TurnId).ToDictionary(group => group.Key, group => group.Last().Id);
        var toolsByMessage = IndexTools(request.ToolRuns);
        var items = new List<TranscriptRenderItem>(messages.Length + turns.Count);
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
            if (!lastAssistants.ContainsKey(turn.Id) && lastMessages[turn.Id] == message.Id)
            {
                items.Add(BuildOutcomeItem(turn));
            }
        }

        foreach (var turn in request.Turns.Where(turn => !lastMessages.ContainsKey(turn.Id)))
        {
            items.Add(BuildOutcomeItem(turn));
        }

        var liveMessages = messages.Select(message => message.Id).ToHashSet();
        foreach (var id in _messageCache.Keys.Where(id => !liveMessages.Contains(id)).ToArray()) _messageCache.Remove(id);
        foreach (var id in _outcomeCache.Keys.Where(id => !turns.ContainsKey(id) || lastAssistants.ContainsKey(id)).ToArray()) _outcomeCache.Remove(id);
        return items;
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
