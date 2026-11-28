
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.AnthropicMessages;
using SelfClaw.LlmProvider.Protocols.OpenAiChat;
using SelfClaw.LlmProvider.Protocols.OpenAiEmbeddings;
using SelfClaw.LlmProvider.Protocols.OpenAiImages;
using SelfClaw.LlmProvider.Protocols.OpenAiResponses;

namespace SelfClaw.LlmProvider.Protocols.Implementations;

/// <summary>按协议 id 索引引擎。重复注册在构造期就失败，问题在启动时暴露而不是在请求时。</summary>
public sealed class LlmProtocolRegistry
{
    private readonly Dictionary<string, ILlmProtocolEngine> _engines;

    public LlmProtocolRegistry(IEnumerable<ILlmProtocolEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        _engines = new Dictionary<string, ILlmProtocolEngine>(StringComparer.Ordinal);
        foreach (var engine in engines)
        {
            if (!_engines.TryAdd(engine.Protocol, engine))
            {
                throw new LlmProviderException(
                    $"Duplicate protocol engine for '{engine.Protocol}': " +
                    $"'{_engines[engine.Protocol].GetType().Name}' and '{engine.GetType().Name}' both registered.");
            }
        }
    }

    /// <summary>五个内置协议。</summary>
    public static LlmProtocolRegistry CreateDefault() =>
        new([
            new OpenAiChatEngine(),
            new OpenAiResponsesEngine(),
            new AnthropicMessagesEngine(),
            new OpenAiImagesEngine(),
            new OpenAiEmbeddingsEngine()
        ]);

    public IReadOnlyCollection<string> Protocols => _engines.Keys;

    public ILlmProtocolEngine GetRequired(string protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        return _engines.TryGetValue(protocol, out var engine)
            ? engine
            : throw new LlmProviderException(
                $"No protocol engine is registered for '{protocol}'. " +
                $"Known protocols: {string.Join(", ", _engines.Keys.OrderBy(key => key, StringComparer.Ordinal))}.");
    }

    /// <summary>加一个自定义引擎，同名覆盖。用于完全自定义线格式的厂商。</summary>
    public LlmProtocolRegistry With(ILlmProtocolEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var copy = new Dictionary<string, ILlmProtocolEngine>(_engines, StringComparer.Ordinal)
        {
            [engine.Protocol] = engine
        };
        return new LlmProtocolRegistry(copy.Values);
    }
}
