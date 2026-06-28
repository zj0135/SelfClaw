using System.Text.Json;
using System.Text.Json.Nodes;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions;

/// <summary>
/// The <see cref="CliAgentDefinition"/> for Claude Code (plan.md 阶段 4, T4.2), mirroring Open Design's
/// <c>runtimes/defs/claude.ts</c>.
/// <para>
/// Launched as <c>claude -p --input-format stream-json --output-format stream-json --verbose</c>:
/// <list type="bullet">
///   <item><c>-p</c> — non-interactive (print) mode.</item>
///   <item><c>--input-format stream-json</c> — the prompt is delivered as JSONL <c>user</c> messages on stdin.</item>
///   <item><c>--output-format stream-json --verbose</c> — Claude emits the full newline-delimited event
///         stream consumed by <c>ClaudeStreamJsonParser</c> (<c>--verbose</c> is required by Claude when
///         combining <c>-p</c> with <c>stream-json</c> output).</item>
/// </list>
/// Session resume uses <see cref="ResumeStrategy.Specified"/>: a UID is minted on the first turn and
/// passed via <c>--session-id</c>; subsequent turns pass <c>--resume &lt;id&gt;</c> (plan.md §6).
/// </para>
/// </summary>
public static class ClaudeAgentDefinition
{
    public static CliAgentDefinition Create() => new()
    {
        Kind = CliAgentKind.Claude,
        Command = "claude",
        InputFormat = CliInputFormat.StreamJson,
        StreamFormat = CliStreamFormat.ClaudeStreamJson,
        ResumeStrategy = ResumeStrategy.Specified,
        BuildArgs = BuildArgs,
        BuildStdinLines = BuildStdinLines,
    };

    private static IReadOnlyList<string> BuildArgs(CliRunContext context)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
        };

        // Resume an existing session when we have one, otherwise pin the freshly minted id so the next
        // turn can resume it (plan.md §6, Specified strategy).
        if (!string.IsNullOrEmpty(context.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(context.ResumeSessionId);
        }
        else if (!string.IsNullOrEmpty(context.NewSessionId))
        {
            args.Add("--session-id");
            args.Add(context.NewSessionId);
        }

        if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(context.SystemPrompt);
        }

        return args;
    }

    /// <summary>
    /// Wraps the user prompt in a single Anthropic <c>user</c> message encoded as one JSONL line, which is
    /// what <c>--input-format stream-json</c> expects. The runtime closes stdin afterwards to end the turn.
    /// </summary>
    private static IReadOnlyList<string> BuildStdinLines(string prompt)
    {
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = prompt,
                    },
                },
            },
        };

        return new[] { message.ToJsonString(JsonOptions) };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Compact single-line output; the JSONL contract is one object per line.
        WriteIndented = false,
    };
}
