using System.Text.Json.Serialization;

namespace SelfClaw.Core.Models;

/// <summary>
/// The single vocabulary for extension readiness. The settings page, the composer's Skill picker and
/// the capability resolver all branch on these values, so they cannot live as loose strings.
/// </summary>
[JsonConverter(typeof(ExtensionStatusJsonConverter))]
public enum ExtensionStatus
{
    Ready,
    Disabled,
    NeedsConfig,
    NeedsPermission,
    Broken
}
