using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters;

internal sealed class OpenCodeCliAgentAdapter : ICliAgentAdapter
{
    public CliAgentKind Kind => CliAgentKind.OpenCode;

    public PreparedCliTurn PrepareTurn(CliTurnPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var arguments = new List<string> { "run", "--format", "json", "--auto" };
        if (!string.IsNullOrEmpty(preparation.StoredSessionId))
        {
            arguments.Add("-s");
            arguments.Add(preparation.StoredSessionId);
        }

        if (!string.IsNullOrWhiteSpace(preparation.Model))
        {
            arguments.Add("--model");
            arguments.Add(preparation.Model);
        }

        return new PreparedCliTurn(
            "opencode",
            arguments,
            new[] { preparation.Prompt },
            new OpenCodeJsonEventStreamParser());
    }
}
