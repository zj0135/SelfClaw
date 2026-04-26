using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private static IReadOnlyList<ChatMessage> BuildPromptMessages(
        IReadOnlyList<MessageRecord> messages,
        bool includeAssistantSpeakerPrefix = true)
        => messages
            .Where(ShouldIncludeInPrompt)
            .Select(message => MapMessage(message, includeAssistantSpeakerPrefix))
            .ToArray();


    private static IReadOnlyList<ChatMessage> BuildCoordinatorPlanningMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<TeamAgentRecord> existingTeamAgents)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(messages));
        if (existingTeamAgents.Count > 0)
        {
            promptMessages.Add(new ChatMessage(
                ChatRole.System,
                "Current active team roster:\n" + string.Join(
                    "\n",
                    existingTeamAgents.OrderBy(agent => agent.SortOrder)
                        .Select(agent => $"- {agent.Name} ({agent.Role})"))));
        }

        return promptMessages;
    }


    private static IReadOnlyList<ChatMessage> BuildBoundAgentPromptMessages(
        IReadOnlyList<MessageRecord> contextMessages,
        IReadOnlyList<MessageRecord> conversationMessages,
        TeamAgentRecord agent)
    {
        var promptMessages = new List<ChatMessage>
        {
            new(
                ChatRole.System,
                $"This dedicated branch is assigned to {agent.Name} ({agent.Role}). Continue as that specialist and use the inherited main conversation only as background context.")
        };

        if (contextMessages.Count > 0)
        {
            promptMessages.Add(new ChatMessage(
                ChatRole.System,
                "Inherited main conversation context begins below. Treat it as read-only background that explains why this branch exists."));
            promptMessages.AddRange(BuildPromptMessages(contextMessages));
        }

        if (conversationMessages.Count > 0)
        {
            promptMessages.Add(new ChatMessage(
                ChatRole.System,
                $"Messages below belong to the dedicated branch with {agent.Name}. Continue this branch directly."));
            promptMessages.AddRange(BuildPromptMessages(conversationMessages));
        }

        return promptMessages;
    }


    private static IReadOnlyList<ChatMessage> BuildWorkerPromptMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<TeamAgentRecord> plannedTeamAgents,
        TeamAgentRecord currentAgent,
        int roundNumber,
        int maxRounds,
        IReadOnlyList<DiscussionEntry> discussionEntries)
    {
        var roster = plannedTeamAgents.Count == 0
            ? $"Current team roster:\n- {currentAgent.Name} ({currentAgent.Role})"
            : "Current team roster:\n" + string.Join(
                "\n",
                plannedTeamAgents.OrderBy(agent => agent.SortOrder)
                    .Select(agent => $"- {agent.Name} ({agent.Role})"));

        var promptMessages = new List<ChatMessage>
        {
            new(ChatRole.System, roster),
            new(ChatRole.System,
                $"You are contributing as {currentAgent.Name} ({currentAgent.Role}). Do not write the final consolidated answer. Focus on your specialty, cite assumptions and risks explicitly, and react to the rest of the team when discussion context is available."),
            new(ChatRole.System, BuildWorkerRoundInstructions(roundNumber, maxRounds))
        };

        promptMessages.AddRange(BuildPromptMessages(messages));

        promptMessages.Add(new ChatMessage(
            ChatRole.System,
            discussionEntries.Count == 0
                ? "No specialist discussion has happened yet."
                : BuildDiscussionTranscript(discussionEntries)));

        return promptMessages;
    }


    private static IReadOnlyList<ChatMessage> BuildCoordinatorSummaryMessages(
        ChatTurnRequest request,
        string documentTitle,
        IReadOnlyList<DiscussionEntry> discussionEntries)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(request.Messages));
        promptMessages.Add(new ChatMessage(ChatRole.System, $"Suggested document title if a file is needed: {documentTitle}"));
        promptMessages.Add(new ChatMessage(
            ChatRole.System,
            discussionEntries.Count == 0
                ? "No specialist discussion replies were produced. Summarize the user request directly and mention that the team discussion was empty."
                : BuildDiscussionTranscript(discussionEntries)));

        return promptMessages;
    }


    private static MessageRecord CreateAssistantMessage(
        Guid conversationId,
        Guid messageId,
        Guid? agentId,
        string agentName,
        string agentRole)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            messageId,
            conversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            agentId,
            agentName,
            agentRole);
    }


    private static bool ShouldIncludeInPrompt(MessageRecord message)
    {
        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            if (string.IsNullOrWhiteSpace(segments.ContentMarkdown))
            {
                return false;
            }
        }

        return message.Status != MessageStatus.Failed;
    }


    private static ChatMessage MapMessage(
        MessageRecord message,
        bool includeAssistantSpeakerPrefix)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;

        if (includeAssistantSpeakerPrefix &&
            message.Role == MessageRole.Assistant &&
            !string.IsNullOrWhiteSpace(message.AgentName))
        {
            var speaker = string.IsNullOrWhiteSpace(message.AgentRole)
                ? message.AgentName
                : $"{message.AgentName} ({message.AgentRole})";
            content = $"[{speaker}]\n{content}";
        }

        if (message.Role == MessageRole.User && message.Attachments is { Count: > 0 } attachments)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrWhiteSpace(content))
            {
                contents.Add(new TextContent(content));
            }

            foreach (var attachment in attachments)
            {
                if (attachment.Kind != MessageAttachmentKind.Image ||
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ||
                    !File.Exists(attachment.StoragePath))
                {
                    continue;
                }

                try
                {
                    contents.Add(new DataContent(File.ReadAllBytes(attachment.StoragePath), attachment.MediaType)
                    {
                        Name = attachment.FileName
                    });
                }
                catch
                {
                    // A missing or unreadable old attachment should not make the whole conversation unusable.
                }
            }

            if (contents.Count > 0)
            {
                return new ChatMessage(MapRole(message.Role), contents);
            }
        }

        return new ChatMessage(MapRole(message.Role), content);
    }


    private static ChatRole MapRole(MessageRole role)
        => role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            _ => ChatRole.Assistant
        };


    private static IReadOnlyList<ChatMessage> BuildExecutionPlanMessages(IReadOnlyList<MessageRecord> messages)
        => BuildPromptMessages(messages, includeAssistantSpeakerPrefix: false);


    private static IReadOnlyList<ChatMessage> BuildExecutionStepMessages(
        IReadOnlyList<MessageRecord> messages,
        ExecutionPlan executionPlan,
        ExecutionPlanStep currentStep,
        IReadOnlyList<CompletedExecutionPlanStep> completedSteps,
        bool isFinalStep)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(messages, includeAssistantSpeakerPrefix: false))
        {
            new(ChatRole.System, BuildExecutionPlanTranscript(executionPlan)),
            new(ChatRole.System, $"Current step id: {currentStep.Id}\nCurrent step title: {currentStep.Title}\nIs final step: {isFinalStep}")
        };

        if (completedSteps.Count == 0)
        {
            promptMessages.Add(new ChatMessage(ChatRole.System, "No prior plan steps have been completed yet."));
            return promptMessages;
        }

        promptMessages.Add(new ChatMessage(ChatRole.System, BuildCompletedExecutionStepTranscript(completedSteps)));
        return promptMessages;
    }


    private static IReadOnlyList<ChatMessage> BuildDocumentDecisionMessages(
        IReadOnlyList<MessageRecord> messages,
        string documentTitle,
        IReadOnlyList<DiscussionEntry> discussionEntries,
        string finalMarkdown)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(messages))
        {
            new(ChatRole.System, $"Candidate document title: {documentTitle}"),
            new(ChatRole.System, discussionEntries.Count == 0 ? "No specialist discussion transcript is available." : BuildDiscussionTranscript(discussionEntries)),
            new(ChatRole.System, "Final team answer:\n" + finalMarkdown)
        };

        return promptMessages;
    }


    private static IReadOnlyList<ChatMessage> BuildRoundContinuationDecisionMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<DiscussionEntry> discussionEntries,
        int currentRound,
        int maxRounds)
    {
        var promptMessages = new List<ChatMessage>(BuildPromptMessages(messages))
        {
            new(ChatRole.System, $"Current round: {currentRound}. Maximum allowed rounds: {maxRounds}."),
            new(ChatRole.System, discussionEntries.Count == 0 ? "No specialist discussion transcript is available." : BuildDiscussionTranscript(discussionEntries))
        };

        return promptMessages;
    }

}
