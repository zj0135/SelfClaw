using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private static TeamBlueprint? TryParseTeamPlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var blueprint = JsonSerializer.Deserialize<TeamBlueprint>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (blueprint is null || blueprint.Agents.Count == 0)
            {
                return null;
            }

            blueprint = blueprint with
            {
                Agents = blueprint.Agents
                    .Where(agent =>
                        !string.IsNullOrWhiteSpace(agent.Name) &&
                        !string.IsNullOrWhiteSpace(agent.Role) &&
                        !string.IsNullOrWhiteSpace(agent.Mission))
                    .Take(MaxTeamAgents - 1)
                    .ToArray()
            };

            return blueprint.Agents.Count == 0 ? null : blueprint;
        }
        catch
        {
            return null;
        }
    }

    private static ExecutionPlanBlueprint? TryParseExecutionPlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var blueprint = JsonSerializer.Deserialize<ExecutionPlanBlueprint>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (blueprint is null || blueprint.Steps.Count == 0)
            {
                return null;
            }

            blueprint = blueprint with
            {
                Steps = blueprint.Steps
                    .Where(step => !string.IsNullOrWhiteSpace(step.Title))
                    .Take(MaxExecutionPlanSteps)
                    .ToArray()
            };

            return blueprint.Steps.Count == 0 ? null : blueprint;
        }
        catch
        {
            return null;
        }
    }

    private static DocumentDecision? TryParseDocumentDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DocumentDecision>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static RoundContinuationDecision? TryParseRoundContinuationDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RoundContinuationDecision>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var fenced = Regex.Match(raw, "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
        {
            return fenced.Groups[1].Value;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static TeamBlueprint BuildFallbackTeamBlueprint(ChatTurnRequest request)
    {
        var latestPrompt = request.Messages.LastOrDefault(message => message.Role == MessageRole.User)?.MarkdownContent ?? "Design discussion";
        var agents = new List<TeamBlueprintAgent>
        {
            new("Product Manager", "Requirements", "Clarify business goals, user scope, and acceptance criteria for the requested design."),
            new("Solution Architect", "Architecture", "Define the overall architecture, module boundaries, and technical trade-offs."),
            new("Security Specialist", "Security", "Review security boundaries, privilege rules, auditing, and abuse risks.")
        };

        if (latestPrompt.Contains("permissions", StringComparison.OrdinalIgnoreCase) ||
            latestPrompt.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            latestPrompt.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            agents.Add(new TeamBlueprintAgent("DBA", "Data Model", "Design tables, indexes, and data constraints needed to support the proposal."));
        }

        return new TeamBlueprint(CreateDocumentTitleFromMessages(request.Messages), agents.Take(MaxTeamAgents - 1).ToArray());
    }

    private static string CreateDocumentTitleFromMessages(IReadOnlyList<MessageRecord> messages)
    {
        var latestPrompt = messages.LastOrDefault(message => message.Role == MessageRole.User)?.MarkdownContent ?? "Team Summary";
        var firstLine = latestPrompt.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Team Summary";
        }

        return firstLine.Length > 36 ? firstLine[..36].Trim() : firstLine;
    }

    private static string CreateTeamDocumentPath(string documentTitle)
    {
        var slug = Slugify(documentTitle);
        return $"docs/selfclaw-team/{slug}.md";
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "team-summary";
        }

        var normalized = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousDash = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
                continue;
            }

            if (previousDash)
            {
                continue;
            }

            builder.Append('-');
            previousDash = true;
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "team-summary" : slug;
    }

    private static string BuildAgentKey(TeamAgentRecord agent)
        => BuildAgentKey(agent.Name, agent.Role);

    private static string BuildAgentKey(string name, string role)
        => $"{name.Trim()}::{role.Trim()}";

}
