using System.Security.Cryptography;
using System.Text;

namespace SelfClaw.Infrastructure.Git;

internal static class GitWorkspaceIdentity
{
    public static Guid RepositoryId(string commonDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(commonDirectory)));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant();
}
