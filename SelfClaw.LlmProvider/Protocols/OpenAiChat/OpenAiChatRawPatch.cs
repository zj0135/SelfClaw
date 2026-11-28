using System.Text;
using System.Text.Json;
using OpenAI.Chat;

#pragma warning disable SCME0001

namespace SelfClaw.LlmProvider.Protocols.OpenAiChat;

/// <summary>
/// 把参数表里的值写进 OpenAI SDK 的原始 JSON 请求体。这是厂商专有字段的通用落地方式：
/// 参数表说"写到哪、什么值"，这里只负责写。
/// </summary>
/// <remarks>
/// 必须对 <c>options.Patch</c> 直接调用 <c>Set</c>：该属性返回的是可写引用，
/// 一旦把 <c>Patch</c> 取出来传给别的方法，改动就落在副本上，请求体里什么也不会出现。
/// </remarks>
internal static class OpenAiChatRawPatch
{
    internal static void Apply(
        ChatCompletionOptions options,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        foreach (var pair in parameters)
        {
            var key = Encoding.UTF8.GetBytes(pair.Key);
            switch (pair.Value.ValueKind)
            {
                case JsonValueKind.String:
                    options.Patch.Set(key, pair.Value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.True:
                    options.Patch.Set(key, true);
                    break;
                case JsonValueKind.False:
                    options.Patch.Set(key, false);
                    break;
                case JsonValueKind.Number when pair.Value.TryGetInt64(out var integer):
                    options.Patch.Set(key, integer);
                    break;
                case JsonValueKind.Number when pair.Value.TryGetDouble(out var real):
                    options.Patch.Set(key, real);
                    break;
            }
        }
    }
}

#pragma warning restore SCME0001
