using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfClaw.Core.Models;

/// <summary>
/// Keeps the WebView wire format ("needs-config") independent of the host's global serializer
/// options, which serialize other enums numerically.
/// </summary>
public sealed class ExtensionStatusJsonConverter : JsonStringEnumConverter<ExtensionStatus>
{
    public ExtensionStatusJsonConverter()
        : base(JsonNamingPolicy.KebabCaseLower)
    {
    }
}
