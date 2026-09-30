using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

/// <summary>
/// A validated view contribution. <see cref="DefaultWidth"/> is only meaningful for a docked view and is
/// therefore null for every other slot — the manifest reader rejects the field rather than ignoring it,
/// so a Plugin author never sees a silently dropped setting.
/// </summary>
internal sealed record PluginViewContribution(
    string Id,
    string Title,
    string Icon,
    PluginViewSlot Slot,
    string Entry,
    int? DefaultWidth);
