using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;

internal sealed record PreparedCliTurn(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> StandardInputLines,
    CliStreamParser Parser);
