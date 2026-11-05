namespace SelfClaw.Core.Models;

/// <summary>
/// Which network exit SelfClaw's own requests use. <see cref="System"/> is deliberately the zero value:
/// it is what the application did before this setting existed (the .NET default handler follows the
/// Windows proxy configuration), so a missing or hand-edited-unreadable node keeps today's behaviour
/// instead of silently going direct.
/// </summary>
public enum NetworkProxyMode
{
    /// <summary>Follow the Windows proxy configuration (the behaviour before this setting existed).</summary>
    System = 0,

    /// <summary>Connect directly, ignoring the system proxy and any inherited proxy variables.</summary>
    Direct = 1,

    /// <summary>Use <see cref="NetworkProxySettings.Host"/> and <see cref="NetworkProxySettings.Port"/>.</summary>
    Custom = 2,
}
