
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Quirks.Abstractions;

namespace SelfClaw.LlmProvider.Quirks.Implementations;

/// <summary>
/// 具名 quirk 的注册表。厂商描述符按 id 引用，未知 id 在请求前就报错。
/// 需要新的请求形状调整时，实现 <see cref="ILlmRequestQuirk"/> 并用
/// <see cref="With"/> 注册。
/// </summary>
public sealed class LlmQuirkRegistry
{
    private readonly Dictionary<string, ILlmRequestQuirk> _quirks;

    public LlmQuirkRegistry(IEnumerable<ILlmRequestQuirk> quirks)
    {
        ArgumentNullException.ThrowIfNull(quirks);
        _quirks = new Dictionary<string, ILlmRequestQuirk>(StringComparer.Ordinal);
        foreach (var quirk in quirks)
        {
            if (!_quirks.TryAdd(quirk.Id, quirk))
            {
                throw new LlmProviderException(
                    $"Duplicate request quirk '{quirk.Id}': " +
                    $"'{_quirks[quirk.Id].GetType().Name}' and '{quirk.GetType().Name}' both registered.");
            }
        }
    }

    /// <summary>
    /// 三个内置 quirk：header-auth、query、path-prefix。它们都是通用的、参数化的请求形状调整。
    /// 针对某家网关错误行为的补偿（如脏 SSE 帧归一）不在这里——那是使用方对自家网关的了解，
    /// 应当由调用方实现 <see cref="ILlmRequestQuirk"/> 后注册。
    /// </summary>
    public static LlmQuirkRegistry CreateDefault() =>
        new([
            new HeaderAuthQuirk(),
            new QueryQuirk(),
            new PathPrefixQuirk()
        ]);

    public IReadOnlyCollection<string> Ids => _quirks.Keys;

    public ILlmRequestQuirk GetRequired(string id) =>
        _quirks.TryGetValue(id, out var quirk)
            ? quirk
            : throw new LlmProviderException($"No request quirk is registered for id '{id}'.");

    public bool Contains(string id) => _quirks.ContainsKey(id);

    /// <summary>加一个自定义 quirk，同 id 覆盖。</summary>
    public LlmQuirkRegistry With(ILlmRequestQuirk quirk)
    {
        ArgumentNullException.ThrowIfNull(quirk);
        var copy = new Dictionary<string, ILlmRequestQuirk>(_quirks, StringComparer.Ordinal)
        {
            [quirk.Id] = quirk
        };
        return new LlmQuirkRegistry(copy.Values);
    }
}
