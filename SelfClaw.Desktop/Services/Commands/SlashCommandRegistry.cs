namespace SelfClaw.Desktop.Services;

public sealed class SlashCommandRegistry
{
    private readonly IReadOnlyList<ISlashCommandHandler> _handlers;

    public SlashCommandRegistry(IEnumerable<ISlashCommandHandler> handlers)
    {
        _handlers = handlers
            .OrderBy(handler => handler.Definition.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<SlashCommandDefinition> Definitions
        => _handlers.Select(handler => handler.Definition).ToArray();

    public ISlashCommandHandler? Resolve(string? command)
    {
        var normalized = NormalizeCommand(command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var normalizedId = normalized.StartsWith("/", StringComparison.Ordinal) ? normalized[1..] : normalized;
        return _handlers.FirstOrDefault(handler =>
            string.Equals(handler.Definition.Command, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(handler.Definition.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCommand(string? command)
    {
        var normalized = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : "/" + normalized;
    }
}
