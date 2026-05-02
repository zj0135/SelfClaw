using System.Globalization;
using System.IO;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string CoordinatorAgentName = "Coordinator";
    private const string CoordinatorRoleName = "Coordinator";

    public async Task SetTeamMaxRoundsAsync(string? roundsId)
    {
        var nextRounds = ParseTeamMaxRounds(roundsId);
        if (SelectedTeamMaxRounds == nextRounds)
        {
            return;
        }

        SelectedTeamMaxRounds = nextRounds;

        if (SelectedConversation is not null)
        {
            await SaveConversationSelectionAsync(SelectedConversation);
        }
    }

    public async Task SetTeamOutputModeAsync(string? outputModeId)
    {
        var nextMode = ParseTeamOutputMode(outputModeId);
        if (SelectedTeamOutputMode == nextMode)
        {
            return;
        }

        SelectedTeamOutputMode = nextMode;

        if (SelectedConversation is not null)
        {
            await SaveConversationSelectionAsync(SelectedConversation);
        }
    }

    private async Task SetConversationModeCoreAsync(ConversationMode nextMode)
    {
        var previousConversation = SelectedConversation;
        var previousConversationHasContent = _messages.Count > 0 || _toolRuns.Count > 0 || _teamAgents.Count > 0;

        if (SelectedConversationMode == nextMode && previousConversation?.Mode == nextMode)
        {
            return;
        }

        SelectedConversationMode = nextMode;
        ClearPlanPanelState(publishShell: false);

        if (IsBusy)
        {
            return;
        }

        var existingConversation = GetFilteredConversations().FirstOrDefault();
        if (existingConversation is not null)
        {
            ApplyConversationFilter(existingConversation.Id);
            return;
        }

        if (nextMode == ConversationMode.Channel)
        {
            ApplyConversationFilter();
            StatusText = "频道会话会在收到外部消息后自动出现。";
            return;
        }

        if (previousConversation is null)
        {
            await CreateNewConversationAsync();
            return;
        }

        if (!previousConversationHasContent)
        {
            var updated = previousConversation with
            {
                Mode = nextMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(updated);
            return;
        }

        await CreateNewConversationAsync();
    }

    private async Task UpsertTeamAgentsAsync(IReadOnlyList<TeamAgentRecord> agents)
    {
        foreach (var agent in agents.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAtUtc))
        {
            var persisted = await _conversationRepository.UpsertTeamAgentAsync(agent);
            UpsertTeamAgent(persisted);
        }

        PublishAgentActivities();
    }

    private async Task UpsertTeamAgentsAsync(ConversationRuntimeState runtimeState, IReadOnlyList<TeamAgentRecord> agents)
    {
        foreach (var agent in agents.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAtUtc))
        {
            var persisted = await _conversationRepository.UpsertTeamAgentAsync(agent);
            UpsertTeamAgent(runtimeState, persisted);
        }

        PublishAgentActivities();
    }

    private async Task UpdateTeamAgentStatusAsync(Guid agentId, TeamAgentStatus status)
    {
        var index = _teamAgents.FindIndex(item => item.Id == agentId);
        if (index < 0)
        {
            return;
        }

        var updated = _teamAgents[index] with
        {
            Status = status,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _teamAgents[index] = updated;
        await _conversationRepository.UpsertTeamAgentAsync(updated);
        PublishAgentActivities();
    }

    private async Task UpdateTeamAgentStatusAsync(ConversationRuntimeState runtimeState, Guid agentId, TeamAgentStatus status)
    {
        var index = runtimeState.TeamAgents.FindIndex(item => item.Id == agentId);
        if (index < 0)
        {
            return;
        }

        var updated = runtimeState.TeamAgents[index] with
        {
            Status = status,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        runtimeState.TeamAgents[index] = updated;
        await _conversationRepository.UpsertTeamAgentAsync(updated);
        PublishAgentActivities();
    }

    private void ApplyAssistantDelta(Guid messageId, string deltaMarkdown)
    {
        if (string.IsNullOrWhiteSpace(deltaMarkdown))
        {
            return;
        }

        var message = _messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return;
        }

        var updated = message with
        {
            MarkdownContent = message.MarkdownContent + deltaMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ReplaceMessage(updated);
        RequestStreamingShellPublish(true);
    }

    private void ApplyAssistantDelta(ConversationRuntimeState runtimeState, Guid messageId, string deltaMarkdown)
    {
        if (string.IsNullOrWhiteSpace(deltaMarkdown))
        {
            return;
        }

        var message = runtimeState.Messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return;
        }

        var updated = message with
        {
            MarkdownContent = message.MarkdownContent + deltaMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ReplaceMessage(runtimeState, updated);
        PublishRuntimeState(runtimeState, true);
    }

    private async Task CompleteAssistantMessageAsync(MessageRecord message)
    {
        var existing = _messages.FirstOrDefault(item => item.Id == message.Id);
        var finalMarkdown = AssistantMessageSegmenter.MergeFinalMarkdown(
            message.MarkdownContent,
            existing?.MarkdownContent);

        var updated = message with
        {
            MarkdownContent = finalMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ReplaceMessage(updated);
        await _conversationRepository.UpsertMessageAsync(updated);
        PublishShellNow(true);
    }

    private async Task CompleteAssistantMessageAsync(ConversationRuntimeState runtimeState, MessageRecord message)
    {
        var existing = runtimeState.Messages.FirstOrDefault(item => item.Id == message.Id);
        var finalMarkdown = AssistantMessageSegmenter.MergeFinalMarkdown(
            message.MarkdownContent,
            existing?.MarkdownContent);

        var updated = message with
        {
            MarkdownContent = finalMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ReplaceMessage(runtimeState, updated);
        await _conversationRepository.UpsertMessageAsync(updated);
        PublishRuntimeStateNow(runtimeState, true);
    }

    private async Task FailActiveMessagesAsync(IEnumerable<Guid> messageIds, string errorMessage)
    {
        foreach (var messageId in messageIds.ToArray())
        {
            var existing = _messages.FirstOrDefault(item => item.Id == messageId);
            if (existing is null)
            {
                continue;
            }

            var updated = existing with
            {
                Status = MessageStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            };

            ReplaceMessage(updated);
            await _conversationRepository.UpsertMessageAsync(updated);
        }
    }

    private async Task FailActiveMessagesAsync(
        ConversationRuntimeState runtimeState,
        IEnumerable<Guid> messageIds,
        string errorMessage)
    {
        foreach (var messageId in messageIds.ToArray())
        {
            var existing = runtimeState.Messages.FirstOrDefault(item => item.Id == messageId);
            if (existing is null)
            {
                continue;
            }

            var updated = existing with
            {
                Status = MessageStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            };

            ReplaceMessage(runtimeState, updated);
            await _conversationRepository.UpsertMessageAsync(updated);
        }
    }

    private async Task FinalizeTeamDocumentExportAsync(
        ConversationRecord conversation,
        TeamDocumentReadyEvent document,
        CancellationToken cancellationToken)
    {
        if (SelectedWorkspaceRoot is null)
        {
            var chatOnlyNote = CreateSystemNote(conversation.Id, "No workspace selected. The team summary remains in chat only.");
            ReplaceMessage(chatOnlyNote);
            await _conversationRepository.UpsertMessageAsync(chatOnlyNote);
            PublishShell(true);
            return;
        }

        var exportRun = CreateExportToolRun(conversation.Id, document);
        UpsertToolRun(exportRun);
        await _conversationRepository.UpsertToolExecutionAsync(exportRun);
        PublishAgentActivities();

        var approved = await _toolApprovalHandler.RequestApprovalAsync(
            new ToolApprovalRequest(
                exportRun.Id,
                exportRun.ToolName,
                "Export Team Document",
                $"Allow SelfClaw to write the team summary to '{document.SuggestedRelativePath}' inside the selected workspace?",
                exportRun.ArgumentsJson,
                conversation.Id),
            cancellationToken);

        if (!approved)
        {
            var denied = exportRun with
            {
                Status = ToolExecutionStatus.Cancelled,
                ResultSummary = "User denied the team document export.",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            UpsertToolRun(denied);
            await _conversationRepository.UpsertToolExecutionAsync(denied);

            var cancelledNote = CreateSystemNote(conversation.Id, "Team summary export was cancelled. The Markdown remains available in chat.");
            ReplaceMessage(cancelledNote);
            await _conversationRepository.UpsertMessageAsync(cancelledNote);
            PublishAgentActivities();
            PublishShell(true);
            return;
        }

        var running = exportRun with
        {
            Status = ToolExecutionStatus.Running,
            ResultSummary = "Export approval granted. Writing Markdown file...",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        UpsertToolRun(running);
        await _conversationRepository.UpsertToolExecutionAsync(running);
        PublishAgentActivities();

        var writeResult = await _workspaceToolService.WriteFileAsync(
            SelectedWorkspaceRoot.RootPath,
            document.SuggestedRelativePath,
            document.MarkdownContent,
            cancellationToken);

        var completed = running with
        {
            Status = writeResult.Applied ? ToolExecutionStatus.Completed : ToolExecutionStatus.Failed,
            ResultSummary = writeResult.Message,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        UpsertToolRun(completed);
        await _conversationRepository.UpsertToolExecutionAsync(completed);

        var noteText = writeResult.Applied
            ? $"Team summary exported to `{document.SuggestedRelativePath}`."
            : $"Team summary export failed: {writeResult.Message}";
        var exportedNote = CreateCoordinatorNote(conversation.Id, noteText);
        ReplaceMessage(exportedNote);
        await _conversationRepository.UpsertMessageAsync(exportedNote);
        PublishAgentActivities();
        PublishShell(true);
    }

    private async Task FinalizeTeamDocumentExportAsync(
        ConversationRuntimeState runtimeState,
        ConversationRecord conversation,
        TeamDocumentReadyEvent document,
        WorkspaceRoot? workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (workspaceRoot is null)
        {
            var chatOnlyNote = CreateSystemNote(conversation.Id, "No workspace selected. The team summary remains in chat only.");
            ReplaceMessage(runtimeState, chatOnlyNote);
            await _conversationRepository.UpsertMessageAsync(chatOnlyNote);
            PublishRuntimeState(runtimeState, true);
            return;
        }

        var exportRun = CreateExportToolRun(conversation.Id, document, runtimeState.TeamAgents);
        UpsertToolRun(runtimeState, exportRun);
        await _conversationRepository.UpsertToolExecutionAsync(exportRun);
        PublishAgentActivities();

        var approved = await _toolApprovalHandler.RequestApprovalAsync(
            new ToolApprovalRequest(
                exportRun.Id,
                exportRun.ToolName,
                "Export Team Document",
                $"Allow SelfClaw to write the team summary to '{document.SuggestedRelativePath}' inside the selected workspace?",
                exportRun.ArgumentsJson,
                conversation.Id),
            cancellationToken);

        if (!approved)
        {
            var denied = exportRun with
            {
                Status = ToolExecutionStatus.Cancelled,
                ResultSummary = "User denied the team document export.",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            UpsertToolRun(runtimeState, denied);
            await _conversationRepository.UpsertToolExecutionAsync(denied);

            var cancelledNote = CreateSystemNote(conversation.Id, "Team summary export was cancelled. The Markdown remains available in chat.");
            ReplaceMessage(runtimeState, cancelledNote);
            await _conversationRepository.UpsertMessageAsync(cancelledNote);
            PublishAgentActivities();
            PublishRuntimeState(runtimeState, true);
            return;
        }

        var running = exportRun with
        {
            Status = ToolExecutionStatus.Running,
            ResultSummary = "Export approval granted. Writing Markdown file...",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        UpsertToolRun(runtimeState, running);
        await _conversationRepository.UpsertToolExecutionAsync(running);
        PublishAgentActivities();

        var writeResult = await _workspaceToolService.WriteFileAsync(
            workspaceRoot.RootPath,
            document.SuggestedRelativePath,
            document.MarkdownContent,
            cancellationToken);

        var completed = running with
        {
            Status = writeResult.Applied ? ToolExecutionStatus.Completed : ToolExecutionStatus.Failed,
            ResultSummary = writeResult.Message,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        UpsertToolRun(runtimeState, completed);
        await _conversationRepository.UpsertToolExecutionAsync(completed);

        var noteText = writeResult.Applied
            ? $"Team summary exported to `{document.SuggestedRelativePath}`."
            : $"Team summary export failed: {writeResult.Message}";
        var exportedNote = CreateCoordinatorNote(conversation.Id, noteText, runtimeState.TeamAgents);
        ReplaceMessage(runtimeState, exportedNote);
        await _conversationRepository.UpsertMessageAsync(exportedNote);
        PublishAgentActivities();
        PublishRuntimeState(runtimeState, true);
    }

    private ToolExecutionRecord CaptureToolRunAnchor(ToolExecutionRecord toolRun)
    {
        if (_toolRunAnchors.TryGetValue(toolRun.Id, out var existingAnchor))
        {
            return toolRun with
            {
                MessageId = existingAnchor.MessageId,
                AfterSegmentIndex = existingAnchor.AfterSegmentIndex
            };
        }

        if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
        {
            _toolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            return toolRun;
        }

        if (toolRun.MessageId is not Guid anchoredMessageId)
        {
            return toolRun;
        }

        var message = _messages.FirstOrDefault(item => item.Id == anchoredMessageId);
        if (message is null)
        {
            return toolRun;
        }

        var anchoredMarkdown = AssistantMessageSegmenter.AppendToolAnchor(message.MarkdownContent, toolRun.Id);
        var anchoredSegments = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(anchoredMarkdown).Segments
            : [];
        var anchorIndex = anchoredSegments
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry =>
                entry.item.Kind == AssistantMessageSegmentKind.ToolAnchor &&
                entry.item.ToolExecutionId == toolRun.Id)
            .index;
        var anchorAfterSegmentIndex = anchorIndex > 0 ? anchorIndex - 1 : -1;
        var anchor = new ToolRunAnchor(anchoredMessageId, anchorAfterSegmentIndex);

        ReplaceMessage(message with
        {
            MarkdownContent = anchoredMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        _toolRunAnchors[toolRun.Id] = anchor;
        return toolRun with
        {
            MessageId = anchor.MessageId,
            AfterSegmentIndex = anchor.AfterSegmentIndex
        };
    }

    private ToolExecutionRecord CaptureToolRunAnchor(ConversationRuntimeState runtimeState, ToolExecutionRecord toolRun)
    {
        if (runtimeState.ToolRunAnchors.TryGetValue(toolRun.Id, out var existingAnchor))
        {
            return toolRun with
            {
                MessageId = existingAnchor.MessageId,
                AfterSegmentIndex = existingAnchor.AfterSegmentIndex
            };
        }

        if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
        {
            runtimeState.ToolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            return toolRun;
        }

        if (toolRun.MessageId is not Guid anchoredMessageId)
        {
            return toolRun;
        }

        var message = runtimeState.Messages.FirstOrDefault(item => item.Id == anchoredMessageId);
        if (message is null)
        {
            return toolRun;
        }

        var anchoredMarkdown = AssistantMessageSegmenter.AppendToolAnchor(message.MarkdownContent, toolRun.Id);
        var anchoredSegments = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(anchoredMarkdown).Segments
            : [];
        var anchorIndex = anchoredSegments
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry =>
                entry.item.Kind == AssistantMessageSegmentKind.ToolAnchor &&
                entry.item.ToolExecutionId == toolRun.Id)
            .index;
        var anchorAfterSegmentIndex = anchorIndex > 0 ? anchorIndex - 1 : -1;
        var anchor = new ToolRunAnchor(anchoredMessageId, anchorAfterSegmentIndex);

        ReplaceMessage(runtimeState, message with
        {
            MarkdownContent = anchoredMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        runtimeState.ToolRunAnchors[toolRun.Id] = anchor;
        return toolRun with
        {
            MessageId = anchor.MessageId,
            AfterSegmentIndex = anchor.AfterSegmentIndex
        };
    }

    private void UpsertTeamAgent(TeamAgentRecord record)
    {
        var index = _teamAgents.FindIndex(item => item.Id == record.Id);
        if (index >= 0)
        {
            _teamAgents[index] = record;
        }
        else
        {
            _teamAgents.Add(record);
        }
    }

    private static void UpsertTeamAgent(ConversationRuntimeState runtimeState, TeamAgentRecord record)
    {
        var index = runtimeState.TeamAgents.FindIndex(item => item.Id == record.Id);
        if (index >= 0)
        {
            runtimeState.TeamAgents[index] = record;
        }
        else
        {
            runtimeState.TeamAgents.Add(record);
        }
    }

    private static ConversationMode ParseConversationMode(string? modeId)
        => modeId?.Trim().ToLowerInvariant() switch
        {
            "team" => ConversationMode.Team,
            "channel" => ConversationMode.Channel,
            _ => ConversationMode.Programming
        };

    private static string ConversationModeToId(ConversationMode mode)
        => mode switch
        {
            ConversationMode.Team => "team",
            ConversationMode.Channel => "channel",
            _ => "programming"
        };

    private static string TeamOutputModeToId(TeamOutputMode mode)
        => mode switch
        {
            TeamOutputMode.ReplyOnly => "replyOnly",
            TeamOutputMode.AlwaysDocument => "alwaysDocument",
            _ => "autoDocument"
        };

    private static int ParseTeamMaxRounds(string? roundsId)
        => int.TryParse(roundsId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? TeamDiscussionDefaults.ClampRounds(parsed)
            : TeamDiscussionDefaults.DefaultMaxRounds;

    private static TeamOutputMode ParseTeamOutputMode(string? outputModeId)
        => outputModeId?.Trim().ToLowerInvariant() switch
        {
            "replyonly" => TeamOutputMode.ReplyOnly,
            "alwaysdocument" => TeamOutputMode.AlwaysDocument,
            _ => TeamOutputMode.AutoDocument
        };

    private static AgentActivityNode BuildTeamMemberActivityNode(TeamAgentRecord agent)
        => new(
            agent.Id.ToString("D"),
            "team-member",
            "Team member",
            agent.Status.ToString().ToLowerInvariant(),
            agent.Status.ToString(),
            agent.Name,
            agent.Role,
            agent.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            [
                new AgentActivityDetail("Role", agent.Role),
                new AgentActivityDetail("Prompt", agent.GoalPrompt),
                new AgentActivityDetail("Status", agent.Status.ToString())
            ],
            agent.Id.ToString("D"));

    private static AgentActivityNode BuildTeamAgentEventNode(TeamAgentRecord agent)
        => new(
            "event-" + agent.Id.ToString("D"),
            "team-event",
            "Team event",
            agent.Status.ToString().ToLowerInvariant(),
            agent.Status.ToString(),
            agent.Name,
            BuildTeamEventSummary(agent),
            agent.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            [
                new AgentActivityDetail("Role", agent.Role),
                new AgentActivityDetail("Status", agent.Status.ToString()),
                new AgentActivityDetail("Prompt", agent.GoalPrompt)
            ],
            agent.Id.ToString("D"));

    private static string BuildTeamEventSummary(TeamAgentRecord agent)
        => agent.Status switch
        {
            TeamAgentStatus.Ready => $"{agent.Role} 已就绪，等待参与讨论。",
            TeamAgentStatus.Running => $"{agent.Role} 正在输出当前轮次意见。",
            TeamAgentStatus.Completed => $"{agent.Role} 已完成当前阶段反馈。",
            TeamAgentStatus.Failed => $"{agent.Role} 在本轮处理时失败。",
            _ => agent.Role
        };

    private MessageRecord CreateSystemNote(Guid conversationId, string content)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.System,
            content,
            MessageStatus.Completed,
            now,
            now);
    }

    private MessageRecord CreateCoordinatorNote(Guid conversationId, string content)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            content,
            MessageStatus.Completed,
            now,
            now,
            _teamAgents.FirstOrDefault(agent => string.Equals(agent.Role, CoordinatorRoleName, StringComparison.OrdinalIgnoreCase))?.Id,
            CoordinatorAgentName,
            CoordinatorRoleName);
    }

    private static MessageRecord CreateCoordinatorNote(
        Guid conversationId,
        string content,
        IReadOnlyList<TeamAgentRecord> teamAgents)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            content,
            MessageStatus.Completed,
            now,
            now,
            teamAgents.FirstOrDefault(agent => string.Equals(agent.Role, CoordinatorRoleName, StringComparison.OrdinalIgnoreCase))?.Id,
            CoordinatorAgentName,
            CoordinatorRoleName);
    }

    private ToolExecutionRecord CreateExportToolRun(Guid conversationId, TeamDocumentReadyEvent document)
    {
        var coordinatorMessageId = document.MessageId;
        var argumentsJson = $"{{\"relativePath\":\"{EscapeJson(document.SuggestedRelativePath)}\"}}";
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "export_team_document",
            argumentsJson,
            ToolExecutionStatus.AwaitingApproval,
            "Waiting for your confirmation to write the team summary Markdown file.",
            null,
            null,
            now,
            now,
            _teamAgents.FirstOrDefault(agent => string.Equals(agent.Role, CoordinatorRoleName, StringComparison.OrdinalIgnoreCase))?.Id,
            coordinatorMessageId,
            null);
    }

    private static ToolExecutionRecord CreateExportToolRun(
        Guid conversationId,
        TeamDocumentReadyEvent document,
        IReadOnlyList<TeamAgentRecord> teamAgents)
    {
        var coordinatorMessageId = document.MessageId;
        var argumentsJson = $"{{\"relativePath\":\"{EscapeJson(document.SuggestedRelativePath)}\"}}";
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "export_team_document",
            argumentsJson,
            ToolExecutionStatus.AwaitingApproval,
            "Waiting for your confirmation to write the team summary Markdown file.",
            null,
            null,
            now,
            now,
            teamAgents.FirstOrDefault(agent => string.Equals(agent.Role, CoordinatorRoleName, StringComparison.OrdinalIgnoreCase))?.Id,
            coordinatorMessageId,
            null);
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string BuildAvatarText(MessageRecord message)
    {
        if (message.Role == MessageRole.User)
        {
            return "You";
        }

        if (!string.IsNullOrWhiteSpace(message.AgentName))
        {
            var tokens = message.AgentName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2)
            {
                return string.Concat(tokens[0][0], tokens[1][0]).ToUpperInvariant();
            }

            return message.AgentName.Length <= 2
                ? message.AgentName.ToUpperInvariant()
                : message.AgentName[..2].ToUpperInvariant();
        }

        return message.Role == MessageRole.Assistant ? "SC" : "SYS";
    }
}
