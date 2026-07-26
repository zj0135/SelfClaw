using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Extensions.Models;

internal sealed record ExtensionPackageInstallResult(
    ExtensionPackageRecord Package,
    int FileCount);
