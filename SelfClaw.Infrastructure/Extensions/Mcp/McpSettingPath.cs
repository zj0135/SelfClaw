namespace SelfClaw.Infrastructure.Extensions.Mcp;

/// <summary>
/// Owns the "environment.KEY" / "headers.KEY" path syntax shared by MCP settings, credential refs and
/// required-field lists, including the key comparison rules: environment variable names are matched
/// case-sensitively when persisted and case-insensitively when handed to a process, while HTTP header
/// names are always case-insensitive.
/// </summary>
internal static class McpSettingPath
{
    public const string EnvironmentPrefix = "environment";
    public const string HeaderPrefix = "headers";

    public static string ForEnvironment(string key) => $"{EnvironmentPrefix}.{key}";

    public static string ForHeader(string key) => $"{HeaderPrefix}.{key}";

    /// <summary>Builds a path from a plugin manifest's <c>env</c>/<c>header</c> target.</summary>
    public static string ForManifestTarget(string target, string key)
        => target == "env" ? ForEnvironment(key) : ForHeader(key);

    public static bool TryParse(string path, out bool isEnvironment, out string key)
    {
        if (TrySplit(path, EnvironmentPrefix, out key))
        {
            isEnvironment = true;
            return true;
        }

        isEnvironment = false;
        return TrySplit(path, HeaderPrefix, out key);
    }

    public static IEnumerable<string> KeysUnder(IEnumerable<string> paths, string prefix)
        => paths.Where(path => path.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Select(path => path[(prefix.Length + 1)..]);

    public static Dictionary<string, string> CreateEnvironment(IEnumerable<KeyValuePair<string, string>>? values = null)
        => Create(values, StringComparer.Ordinal);

    public static Dictionary<string, string> CreateHeaders(IEnumerable<KeyValuePair<string, string>>? values = null)
        => Create(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Environment names are case-insensitive once they reach a Windows process, so the resolved view
    /// must collapse duplicates that the persisted (ordinal) map allows.
    /// </summary>
    public static Dictionary<string, string> CreateResolvedEnvironment(
        IEnumerable<KeyValuePair<string, string>> values)
        => Create(values, StringComparer.OrdinalIgnoreCase);

    public static bool IsValidEnvironmentKey(string? key)
        => !string.IsNullOrWhiteSpace(key) &&
            (char.IsAsciiLetter(key[0]) || key[0] == '_') &&
            key.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static bool IsValidHeaderName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_'
                or '`' or '|' or '~');

    private static bool TrySplit(string path, string prefix, out string key)
    {
        if (path.StartsWith(prefix + ".", StringComparison.Ordinal) && path.Length > prefix.Length + 1)
        {
            key = path[(prefix.Length + 1)..];
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static Dictionary<string, string> Create(
        IEnumerable<KeyValuePair<string, string>>? values,
        StringComparer comparer)
        => values is null
            ? new Dictionary<string, string>(comparer)
            : new Dictionary<string, string>(values, comparer);
}
