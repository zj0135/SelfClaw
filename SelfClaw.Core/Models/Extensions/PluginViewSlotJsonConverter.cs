using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfClaw.Core.Models;

/// <summary>
/// Keeps the WebView wire format ("right" / "floating") independent of the host's global serializer
/// options, which serialize other enums numerically. The shell reads this value to decide which
/// container renders the frame.
/// </summary>
public sealed class PluginViewSlotJsonConverter : JsonStringEnumConverter<PluginViewSlot>
{
    public PluginViewSlotJsonConverter()
        : base(JsonNamingPolicy.KebabCaseLower)
    {
    }
}
