using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters;

internal sealed class CodexCliAgentAdapter : ICliAgentAdapter
{
    public CliAgentKind Kind => CliAgentKind.Codex;

    public PreparedCliTurn PrepareTurn(CliTurnPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var arguments = new List<string> { "exec" };
        if (!string.IsNullOrEmpty(preparation.StoredSessionId))
        {
            arguments.Add("resume");
            arguments.Add(preparation.StoredSessionId);
        }

        arguments.Add("--json");
        arguments.Add("--skip-git-repo-check");
        arguments.Add("--dangerously-bypass-approvals-and-sandbox");

        if (!string.IsNullOrWhiteSpace(preparation.Model))
        {
            arguments.Add("--model");
            arguments.Add(preparation.Model);
        }

        if (!string.IsNullOrWhiteSpace(preparation.ReasoningEffort))
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{preparation.ReasoningEffort}\"");
        }

        return new PreparedCliTurn(
            "codex",
            arguments,
            new[] { preparation.Prompt },
            new CodexJsonEventStreamParser());
    }
}
