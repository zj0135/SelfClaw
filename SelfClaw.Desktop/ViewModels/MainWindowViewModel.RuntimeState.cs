using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private sealed class ConversationRuntimeState : IDisposable
    {
        public ConversationRuntimeState(
            ConversationRecord conversation,
            IEnumerable<MessageRecord> messages,
            IEnumerable<ToolExecutionRecord> toolRuns,
            IReadOnlyDictionary<Guid, ToolRunAnchor> toolRunAnchors)
        {
            Conversation = conversation;
            Messages.AddRange(messages);
            ToolRuns.AddRange(toolRuns);
            foreach (var item in toolRunAnchors)
            {
                ToolRunAnchors[item.Key] = item.Value;
            }
        }

        public ConversationRecord Conversation { get; set; }

        public Guid ConversationId => Conversation.Id;

        public List<MessageRecord> Messages { get; } = [];

        public List<ToolExecutionRecord> ToolRuns { get; } = [];

        public Dictionary<Guid, ToolRunAnchor> ToolRunAnchors { get; } = [];

        public HashSet<Guid> ActiveMessageIds { get; } = [];

        /// <summary>Latest RunStatusEvent text, shown while the streaming message has no content yet.</summary>
        public string? ActivityText { get; set; }

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public bool IsRunning { get; set; } = true;

        public Task Completion => _completion.Task;

        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkCompleted() => _completion.TrySetResult();

        public void Dispose() => CancellationTokenSource.Dispose();
    }

    private ConversationRuntimeState? GetSelectedRuntimeState()
        => SelectedConversation is { } conversation &&
           _conversationRuntimeStates.TryGetValue(conversation.Id, out var state)
            ? state
            : null;

    private bool IsSelectedConversation(Guid conversationId)
        => SelectedConversation?.Id == conversationId;

    private bool IsConversationRunning(Guid conversationId)
        => _conversationRuntimeStates.TryGetValue(conversationId, out var state) && state.IsRunning;

    private bool IsSelectedConversationRunning()
        => GetSelectedRuntimeState()?.IsRunning == true;

    private ConversationRuntimeState StartConversationRuntimeState(
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord>? messages = null,
        IReadOnlyList<ToolExecutionRecord>? toolRuns = null,
        IReadOnlyDictionary<Guid, ToolRunAnchor>? toolRunAnchors = null)
    {
        if (_conversationRuntimeStates.TryGetValue(conversation.Id, out var existing))
        {
            if (existing.IsRunning)
            {
                throw new InvalidOperationException("This conversation is already running.");
            }

            existing.Dispose();
        }

        var sourceState = existing;
        var state = new ConversationRuntimeState(
            conversation,
            sourceState?.Messages ?? messages ?? _messages,
            sourceState?.ToolRuns ?? toolRuns ?? _toolRuns,
            sourceState?.ToolRunAnchors ?? toolRunAnchors ?? _toolRunAnchors);

        _conversationRuntimeStates[conversation.Id] = state;

        if (IsSelectedConversation(conversation.Id))
        {
            SyncSelectedDisplayStateFromRuntime(state);
            PublishShell(false);
        }

        return state;
    }

    private void CompleteConversationRuntimeState(ConversationRuntimeState state)
    {
        state.IsRunning = false;
        state.MarkCompleted();
        if (IsSelectedConversation(state.ConversationId))
        {
            SyncSelectedDisplayStateFromRuntime(state);
            PublishShellNow(true);
        }
    }

    private void SyncSelectedDisplayStateFromRuntime(ConversationRuntimeState state)
    {
        ReplaceList(_messages, state.Messages);
        ReplaceList(_toolRuns, state.ToolRuns);
        _toolRunAnchors.Clear();
        foreach (var item in state.ToolRunAnchors)
        {
            _toolRunAnchors[item.Key] = item.Value;
        }
    }

    private void PublishRuntimeState(ConversationRuntimeState state, bool autoScroll)
    {
        if (!IsSelectedConversation(state.ConversationId))
        {
            return;
        }

        SyncSelectedDisplayStateFromRuntime(state);
        RequestStreamingShellPublish(autoScroll);
    }

    private void PublishRuntimeStateNow(ConversationRuntimeState state, bool autoScroll)
    {
        if (!IsSelectedConversation(state.ConversationId))
        {
            return;
        }

        SyncSelectedDisplayStateFromRuntime(state);
        PublishShellNow(autoScroll);
    }

    private IReadOnlyList<MessageRecord> GetSelectedTranscriptMessages()
        => GetSelectedRuntimeState()?.Messages ?? _messages;

    private IReadOnlyList<ToolExecutionRecord> GetSelectedTranscriptToolRuns()
        => GetSelectedRuntimeState()?.ToolRuns ?? _toolRuns;

    private IReadOnlyDictionary<Guid, ToolRunAnchor> GetSelectedTranscriptToolRunAnchors()
        => GetSelectedRuntimeState()?.ToolRunAnchors ?? _toolRunAnchors;

    private static void ReplaceList<T>(List<T> target, IEnumerable<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }
}
