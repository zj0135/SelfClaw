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
            IEnumerable<MessageRecord> contextMessages,
            IEnumerable<TeamAgentRecord> teamAgents,
            IEnumerable<ToolExecutionRecord> toolRuns,
            IReadOnlyDictionary<Guid, ToolRunAnchor> toolRunAnchors,
            bool usePlanningMode,
            string statusText)
        {
            Conversation = conversation;
            Messages.AddRange(messages);
            ContextMessages.AddRange(contextMessages);
            TeamAgents.AddRange(teamAgents);
            ToolRuns.AddRange(toolRuns);
            foreach (var item in toolRunAnchors)
            {
                ToolRunAnchors[item.Key] = item.Value;
            }

            UsePlanningMode = usePlanningMode;
            StatusText = statusText;
        }

        public ConversationRecord Conversation { get; set; }

        public Guid ConversationId => Conversation.Id;

        public List<MessageRecord> Messages { get; } = [];

        public List<MessageRecord> ContextMessages { get; } = [];

        public List<TeamAgentRecord> TeamAgents { get; } = [];

        public List<ToolExecutionRecord> ToolRuns { get; } = [];

        public Dictionary<Guid, ToolRunAnchor> ToolRunAnchors { get; } = [];

        public HashSet<Guid> ActiveMessageIds { get; } = [];

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public bool UsePlanningMode { get; }

        public string StatusText { get; set; }

        public bool IsRunning { get; set; } = true;

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

    private bool ResolvePlanningMode(Guid conversationId)
        => _conversationPlanningModes.TryGetValue(conversationId, out var enabled) && enabled;

    private void SetPlanningModeForSelectedConversation(bool enabled, bool publishShell)
    {
        if (SelectedConversation is { } conversation)
        {
            _conversationPlanningModes[conversation.Id] = enabled;
        }

        if (SetProperty(ref _isPlanningModeEnabled, enabled, nameof(IsPlanningModeEnabled)) && publishShell)
        {
            PublishShell(false);
        }
    }

    private void ProjectSelectedRuntimeState(bool publishShell)
    {
        var runtimeState = GetSelectedRuntimeState();
        var nextBusy = runtimeState?.IsRunning == true;
        if (SetProperty(ref _isBusy, nextBusy, nameof(IsBusy)))
        {
            NotifyCommandStates();
        }

        if (SelectedConversation is { } conversation)
        {
            var nextPlanningMode = ResolvePlanningMode(conversation.Id);
            SetProperty(ref _isPlanningModeEnabled, nextPlanningMode, nameof(IsPlanningModeEnabled));

            if (runtimeState is not null)
            {
                SetProperty(ref _statusText, runtimeState.StatusText, nameof(StatusText));
            }
            else if (_conversationStatusTexts.TryGetValue(conversation.Id, out var statusText))
            {
                SetProperty(ref _statusText, statusText, nameof(StatusText));
            }
        }
        else
        {
            SetProperty(ref _isPlanningModeEnabled, false, nameof(IsPlanningModeEnabled));
        }

        NotifyCommandStates();
        if (publishShell)
        {
            PublishShell(false);
        }
    }

    private void SetStatusTextForSelectedConversation(string statusText, bool publishShell)
    {
        if (SelectedConversation is { } conversation)
        {
            _conversationStatusTexts[conversation.Id] = statusText;
            if (_conversationRuntimeStates.TryGetValue(conversation.Id, out var runtimeState))
            {
                runtimeState.StatusText = statusText;
            }
        }

        if (SetProperty(ref _statusText, statusText, nameof(StatusText)) && publishShell)
        {
            PublishShell(false);
        }
    }

    private void SetStatusTextForConversation(ConversationRuntimeState state, string statusText, bool publishShell)
    {
        state.StatusText = statusText;
        _conversationStatusTexts[state.ConversationId] = statusText;

        if (IsSelectedConversation(state.ConversationId))
        {
            SetProperty(ref _statusText, statusText, nameof(StatusText));
            if (publishShell)
            {
                PublishShell(false);
            }
        }
    }

    private ConversationRuntimeState StartConversationRuntimeState(
        ConversationRecord conversation,
        bool usePlanningMode,
        string statusText,
        IReadOnlyList<MessageRecord>? messages = null,
        IReadOnlyList<MessageRecord>? contextMessages = null,
        IReadOnlyList<TeamAgentRecord>? teamAgents = null,
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
            sourceState?.ContextMessages ?? contextMessages ?? _contextMessages,
            sourceState?.TeamAgents ?? teamAgents ?? _teamAgents,
            sourceState?.ToolRuns ?? toolRuns ?? _toolRuns,
            sourceState?.ToolRunAnchors ?? toolRunAnchors ?? _toolRunAnchors,
            usePlanningMode,
            statusText);

        _conversationRuntimeStates[conversation.Id] = state;
        _conversationStatusTexts[conversation.Id] = statusText;
        _conversationPlanPanels[conversation.Id] = TranscriptPlanPanel.Hidden;

        if (IsSelectedConversation(conversation.Id))
        {
            SyncSelectedDisplayStateFromRuntime(state);
            ProjectSelectedRuntimeState(publishShell: true);
        }

        return state;
    }

    private void CompleteConversationRuntimeState(ConversationRuntimeState state)
    {
        state.IsRunning = false;
        if (IsSelectedConversation(state.ConversationId))
        {
            SyncSelectedDisplayStateFromRuntime(state);
            ProjectSelectedRuntimeState(publishShell: false);
            PublishShellNow(true);
        }
    }

    private void SyncSelectedDisplayStateFromRuntime(ConversationRuntimeState state)
    {
        ReplaceList(_messages, state.Messages);
        ReplaceList(_contextMessages, state.ContextMessages);
        ReplaceList(_teamAgents, state.TeamAgents);
        ReplaceList(_toolRuns, state.ToolRuns);
        _toolRunAnchors.Clear();
        foreach (var item in state.ToolRunAnchors)
        {
            _toolRunAnchors[item.Key] = item.Value;
        }

        _selectedBoundAgent = ResolveBoundAgent(state.Conversation, state.TeamAgents);
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

    private IReadOnlyList<MessageRecord> GetSelectedTranscriptContextMessages()
        => GetSelectedRuntimeState()?.ContextMessages ?? _contextMessages;

    private IReadOnlyList<TeamAgentRecord> GetSelectedTranscriptTeamAgents()
        => GetSelectedRuntimeState()?.TeamAgents ?? _teamAgents;

    private IReadOnlyList<ToolExecutionRecord> GetSelectedTranscriptToolRuns()
        => GetSelectedRuntimeState()?.ToolRuns ?? _toolRuns;

    private IReadOnlyDictionary<Guid, ToolRunAnchor> GetSelectedTranscriptToolRunAnchors()
        => GetSelectedRuntimeState()?.ToolRunAnchors ?? _toolRunAnchors;

    private TranscriptPlanPanel GetSelectedPlanPanel()
    {
        if (SelectedConversation is not { } conversation ||
            SelectedConversationMode != ConversationMode.Programming)
        {
            return TranscriptPlanPanel.Hidden;
        }

        return _conversationPlanPanels.TryGetValue(conversation.Id, out var planPanel)
            ? planPanel
            : TranscriptPlanPanel.Hidden;
    }

    private string GetSelectedStatusText()
    {
        if (SelectedConversation is not { } conversation)
        {
            return StatusText;
        }

        if (_conversationRuntimeStates.TryGetValue(conversation.Id, out var runtimeState))
        {
            return runtimeState.StatusText;
        }

        return _conversationStatusTexts.TryGetValue(conversation.Id, out var statusText)
            ? statusText
            : StatusText;
    }

    private static void ReplaceList<T>(List<T> target, IEnumerable<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }
}
