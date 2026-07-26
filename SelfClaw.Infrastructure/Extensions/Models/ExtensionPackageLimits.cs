namespace SelfClaw.Infrastructure.Extensions.Models;

internal sealed record ExtensionPackageLimits(
    long MaximumArchiveBytes,
    long MaximumExpandedBytes,
    int MaximumFileCount,
    long MaximumFileBytes,
    long MaximumManifestBytes);
