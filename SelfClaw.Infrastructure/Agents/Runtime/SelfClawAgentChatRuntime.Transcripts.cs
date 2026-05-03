using System.Text;
using System.Text.RegularExpressions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private static string BuildExecutionPlanTranscript(ExecutionPlan executionPlan)
    {
        var transcript = new StringBuilder();
        transcript.AppendLine("Execution plan:");
        if (!string.IsNullOrWhiteSpace(executionPlan.Summary))
        {
            transcript.AppendLine(executionPlan.Summary.Trim());
        }

        foreach (var step in executionPlan.Steps)
        {
            transcript.AppendLine($"- [{step.Id}] {step.Title}");
        }

        return transcript.ToString();
    }


    private static string BuildCompletedExecutionStepTranscript(IReadOnlyList<CompletedExecutionPlanStep> completedSteps)
    {
        var transcript = new StringBuilder();
        transcript.AppendLine("Completed execution notes:");

        foreach (var step in completedSteps)
        {
            transcript.AppendLine();
            transcript.AppendLine($"## [{step.Step.Id}] {step.Step.Title}");
            transcript.AppendLine(step.Markdown);
        }

        return transcript.ToString();
    }


    private static string BuildExecutionPlanSummary(ChatTurnRequest request, string? rawSummary)
    {
        var sanitizedSummary = SanitizeExecutionPlanText(rawSummary);
        if (!string.IsNullOrWhiteSpace(sanitizedSummary))
        {
            var summary = sanitizedSummary.ReplaceLineEndings(" ").Trim();
            return summary.Length > 160 ? summary[..160].TrimEnd() + "..." : summary;
        }

        var latestPrompt = request.Messages.LastOrDefault(message => message.Role == MessageRole.User)?.MarkdownContent;
        if (!string.IsNullOrWhiteSpace(latestPrompt))
        {
            var normalized = latestPrompt.ReplaceLineEndings(" ").Trim();
            if (normalized.Length > 80)
            {
                normalized = normalized[..80].TrimEnd() + "...";
            }

            return $"Execution plan focused on \"{normalized}\".";
        }

        return "Execute the current request step by step, then summarize the final result.";
    }


    private static string? SanitizeExecutionPlanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var strippedThinking = Regex.Replace(
            raw,
            @"(?is)<!--selfclaw:think:start-->.*?<!--selfclaw:think:end-->",
            " ");
        strippedThinking = Regex.Replace(
            strippedThinking,
            @"(?is)<!--selfclaw:think:start-->.*$",
            " ");
        strippedThinking = strippedThinking.Replace("<!--selfclaw:think:end-->", " ", StringComparison.OrdinalIgnoreCase);

        var visibleContent = AssistantMessageSegmenter.Split(strippedThinking).ContentMarkdown;
        if (string.IsNullOrWhiteSpace(visibleContent))
        {
            return null;
        }

        return Regex.Replace(
            visibleContent.ReplaceLineEndings(" ").Trim(),
            @"\s{2,}",
            " ");
    }


    private static string NormalizeExecutionPlanStepId(
        string? rawId,
        string title,
        int index,
        ISet<string> usedIds)
    {
        var baseId = Slugify(string.IsNullOrWhiteSpace(rawId) ? title : rawId);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = $"step-{index}";
        }

        var candidate = baseId;
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

}
