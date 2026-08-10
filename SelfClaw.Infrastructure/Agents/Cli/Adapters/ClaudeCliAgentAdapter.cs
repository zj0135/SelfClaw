using System.Text.Json;
using System.Text.Json.Nodes;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters;

internal sealed class ClaudeCliAgentAdapter : ICliAgentAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public CliAgentKind Kind => CliAgentKind.Claude;

    public PreparedCliTurn PrepareTurn(CliTurnPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var arguments = BuildArguments(preparation);
        var input = BuildInput(preparation.Prompt);
        return new PreparedCliTurn("claude", arguments, input, new ClaudeStreamJsonParser());
    }

    private static IReadOnlyList<string> BuildArguments(CliTurnPreparation preparation)
    {
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--dangerously-skip-permissions",
        };

        if (!string.IsNullOrEmpty(preparation.StoredSessionId))
        {
            arguments.Add("--resume");
            arguments.Add(preparation.StoredSessionId);
        }
        else
        {
            arguments.Add("--session-id");
            arguments.Add(Guid.NewGuid().ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(preparation.Model))
        {
            arguments.Add("--model");
            arguments.Add(preparation.Model);
        }

        if (!string.IsNullOrWhiteSpace(preparation.ReasoningEffort))
        {
            arguments.Add("--effort");
            arguments.Add(preparation.ReasoningEffort);
        }

        if (!string.IsNullOrWhiteSpace(preparation.SystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(preparation.SystemPrompt);
        }

        return arguments;
    }

    private static IReadOnlyList<string> BuildInput(string prompt)
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
}
