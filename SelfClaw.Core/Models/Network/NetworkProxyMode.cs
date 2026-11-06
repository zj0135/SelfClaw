namespace SelfClaw.Core.Models;

/// <summary>
/// Which network exit SelfClaw's own requests use. <see cref="Direct"/> is deliberately the zero value:
/// a fresh install, a missing settings node and a hand-edited-unreadable node all connect directly, so the
/// application never silently inherits whatever proxy the machine happens to have configured.
/// </summary>
public enum NetworkProxyMode
{
    /// <summary>Connect directly, ignoring the system proxy and any inherited proxy variables.</summary>
    Direct = 0,

    /// <summary>Follow the Windows proxy configuration.</summary>
    System = 1,

    /// <summary>Use <see cref="NetworkProxySettings.Host"/> and <see cref="NetworkProxySettings.Port"/>.</summary>
    Custom = 2,
}
