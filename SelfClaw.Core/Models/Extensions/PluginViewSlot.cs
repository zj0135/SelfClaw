using System.Text.Json.Serialization;

namespace SelfClaw.Core.Models;

/// <summary>
/// Where a Plugin view is presented. <see cref="Right"/> is the docked tab column; <see cref="Floating"/>
/// is the transparent window-wide layer a view draws its own chrome into. The slot decides which
/// permission a manifest must declare and which cap the host enforces — nothing else in the host or the
/// transport branches on it.
/// </summary>
[JsonConverter(typeof(PluginViewSlotJsonConverter))]
public enum PluginViewSlot
{
    Right,
    Floating
}
