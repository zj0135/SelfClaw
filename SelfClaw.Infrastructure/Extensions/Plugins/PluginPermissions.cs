namespace SelfClaw.Infrastructure.Extensions.Plugins;

/// <summary>
/// Permissions are a disclosure list the user acknowledges before a Plugin is enabled, so an unknown
/// bare token stays legal — nothing enforces it, and rejecting it would break packages that already
/// declare vocabulary such as <c>workspace.read</c>. A prefixed permission is different:
/// <c>network.fetch:</c> widens the panel CSP directly, so its value is parsed rather than trusted.
/// </summary>
internal static class PluginPermissions
{
    public const string Panel = "ui.panel";
    public const string ContextRead = "host.context.read";
    public const string TranscriptRead = "host.transcript.read";
    public const string ComposerWrite = "host.composer.write";
    public const string WorkspaceRead = "host.workspace.read";
    public const string NetworkFetchPrefix = "network.fetch:";

    private const int MaximumLength = 256;

    /// <summary>
    /// Returns the normalized, distinct, ordinal-sorted permission set. Both the manifest reader and the
    /// enable/acknowledge path run their input through here, so the two can never disagree about whether
    /// a package's permissions changed — a disagreement would leave a Plugin permanently unenablable.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string?>? permissions)
    {
        var values = (permissions ?? [])
            .Select(permission => NormalizeOne(permission?.Trim() ?? string.Empty))
            .ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidDataException("Plugin permissions must be unique.");
        }

        return values.OrderBy(permission => permission, StringComparer.Ordinal).ToArray();
    }

    public static bool Grants(IReadOnlyList<string> permissions, string permission)
        => permissions.Contains(permission, StringComparer.Ordinal);

    /// <summary>
    /// The origins a panel may reach with fetch/XHR/WebSocket. Anything absent is blocked by the panel's
    /// <c>connect-src</c>, so a Plugin that declares nothing is fully offline.
    /// </summary>
    public static IReadOnlyList<string> ReadNetworkOrigins(IReadOnlyList<string> permissions)
        => permissions
            .Where(permission => permission.StartsWith(NetworkFetchPrefix, StringComparison.Ordinal))
            .Select(permission => permission[NetworkFetchPrefix.Length..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeOne(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission) || permission.Length > MaximumLength)
        {
            throw new InvalidDataException("Plugin permissions contain an invalid value.");
        }

        if (!permission.Contains(':', StringComparison.Ordinal))
        {
            if (permission.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            {
                throw new InvalidDataException("Plugin permissions contain an invalid value.");
            }

            return permission;
        }

        if (!permission.StartsWith(NetworkFetchPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Plugin permission '{permission}' uses an unsupported prefix.");
        }

        return NetworkFetchPrefix + NormalizeNetworkOrigin(permission[NetworkFetchPrefix.Length..]);
    }

    // The value must reduce to a bare origin, because that is exactly what a CSP source expression
    // accepts. A path, query or credentials would either be dropped silently by the browser or widen the
    // grant past what the acknowledgement dialog showed. Normalizing here (rather than demanding a
    // canonical spelling from the author) keeps "https://Api.Example.com:443/" and
    // "https://api.example.com" from acknowledging as two different permissions.
    private static string NormalizeNetworkOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath.Trim('/').Length > 0 ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"Plugin permission '{NetworkFetchPrefix}{origin}' must be a bare origin such as https://api.example.com.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidDataException(
                $"Plugin permission '{NetworkFetchPrefix}{origin}' must use HTTPS for non-loopback hosts.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
