using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;

internal interface ICliAgentAdapter
{
    CliAgentKind Kind { get; }

    PreparedCliTurn PrepareTurn(CliTurnPreparation preparation);
}
