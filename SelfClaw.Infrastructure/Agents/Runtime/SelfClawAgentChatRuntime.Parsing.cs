using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
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

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "step";
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
        return string.IsNullOrWhiteSpace(slug) ? "step" : slug;
    }

}
