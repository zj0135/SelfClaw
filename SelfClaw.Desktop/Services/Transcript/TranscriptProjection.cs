using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Transcript;

public sealed class TranscriptProjection
{
    private readonly TranscriptMessageProjector _messageProjector;
    private readonly Dictionary<Guid, (MessageRecord Message, IReadOnlyList<ToolExecutionRecord> Tools, TranscriptRenderItem Item)> _messageCache = [];
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
        var toolsByMessage = IndexTools(request.ToolRuns);
        var liveMessages = new HashSet<Guid>();
        var items = new TranscriptRenderItem[request.Messages.Count];
        var index = 0;
        foreach (var message in request.Messages.OrderBy(message => message.CreatedAtUtc))
        {
            liveMessages.Add(message.Id);
            IReadOnlyList<ToolExecutionRecord> tools = toolsByMessage.TryGetValue(message.Id, out var found) ? found : [];
            items[index++] = BuildMessageItem(message, tools);
        }
        foreach (var id in _messageCache.Keys.Where(id => !liveMessages.Contains(id)).ToArray()) _messageCache.Remove(id);
        _messageProjector.PruneToolSegmentCache(request.ToolRuns);
        UpdateNavigation(request);
        var stableItems = _lastState is { } previous && SameReferences(previous.Items, items) ? previous.Items : items;
        var state = new TranscriptRenderState(stableItems, request.AutoScroll, _navigation,
            request.SelectedConversationId?.ToString("D"), request.IsBusy, request.ActivityText, request.AgentMode,
            request.SelectedAgentId, request.SelectedAgentName, request.CapabilityRevision, request.ToolPermissionMode);
        if (state == _lastState) return null;
        _lastState = state;
        return state;
    }

    internal void Invalidate()
    {
        _lastState = null;
        _messageCache.Clear();
    }

    private TranscriptRenderItem BuildMessageItem(MessageRecord message, IReadOnlyList<ToolExecutionRecord> tools)
    {
        if (_messageCache.TryGetValue(message.Id, out var cached) && ReferenceEquals(cached.Message, message) &&
            SameReferences(cached.Tools, tools)) return cached.Item;
        var item = _messageProjector.Build(message, tools);
        _messageCache[message.Id] = (message, tools, item);
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
