namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// Collects per-turn capability diagnostics. Degradations are the subset the model must be told
/// about (§8.3 section 6); informational entries only reach logs and the activity area.
/// </summary>
internal sealed class TurnDiagnostics
{
    private readonly List<string> _messages = [];
    private readonly List<string> _degradations = [];

    public IReadOnlyList<string> Messages => _messages;
    public IReadOnlyList<string> Degradations => _degradations;

    public void Info(string message) => _messages.Add(message);

    public void Degrade(string message)
    {
        _messages.Add(message);
        _degradations.Add(message);
    }
}
