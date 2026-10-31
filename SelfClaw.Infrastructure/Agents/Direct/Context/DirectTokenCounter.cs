using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// Offline tiktoken counting for prompt tokens. The encoding follows the model passed to each call,
/// because a single instance serves every conversation and model. Differences from a provider's own
/// tokenizer stay within the threshold headroom, and provider-reported usage corrects the judgement
/// on the next turn.
/// </summary>
internal sealed class DirectTokenCounter : IDirectTokenCounter
{
    /// <summary>
    /// Compensates the wire-level role marker and message delimiter that the tokenizer never sees.
    /// This is framing overhead, not an estimate of the message content.
    /// </summary>
    internal const int PerMessageFramingTokens = 4;

    /// <summary>
    /// Compensates the wrapper providers put around a tool definition (the envelope holding name,
    /// description and schema). This is framing overhead, not an estimate of the tool content.
    /// </summary>
    internal const int PerToolFramingTokens = 8;

    /// <summary>
    /// A constant stand-in for one image. The tokenizer cannot size binary content, and leaving it at zero
    /// would let an image-heavy prompt look small enough to skip compaction and the window check.
    /// </summary>
    internal const int PerImageTokens = 1024;

    internal const string DefaultEncodingName = "o200k_base";

    private const string Cl100kBaseEncodingName = "cl100k_base";
    private const int MaximumCachedCounts = 4096;

    private static readonly string[] SupportedEncodingNames = [DefaultEncodingName, Cl100kBaseEncodingName];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        // The counter must weigh what the provider actually receives. The default encoder escapes non-ASCII
        // as \uXXXX and HTML-sensitive characters as \u003C etc., which inflates CJK comments and code
        // snippets in tool results by roughly 1.6x and would both overstate the readout and trigger
        // compaction early.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Func<string, Func<string, int>> _tokenizerFactory;
    private readonly ILogger<DirectTokenCounter> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<string, int>> _tokenizers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedEncodings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUnsupportedEncodings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _messageCounts = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageCountOrder = new();
    private readonly Dictionary<string, int> _toolCounts = new(StringComparer.Ordinal);
    private readonly Queue<string> _toolCountOrder = new();

    /// <param name="logger">Receives at most one warning per failed or unsupported encoding; message bodies are never logged.</param>
    /// <param name="tokenizerFactory">
    /// Test seam: maps an encoding name to a text-counting function. The default loads the vocabulary
    /// from the tokenizer data package, which is embedded in its assembly and never downloaded.
    /// </param>
    internal DirectTokenCounter(
        ILogger<DirectTokenCounter>? logger = null,
        Func<string, Func<string, int>>? tokenizerFactory = null)
    {
        _logger = logger ?? NullLogger<DirectTokenCounter>.Instance;
        _tokenizerFactory = tokenizerFactory ?? CreateTiktokenCounter;
    }

    public bool IsAvailable(AiModelProfile? model) => EnsureCountTokens(ResolveEncodingName(model)) is not null;

    public int? CountText(string? text, AiModelProfile? model)
    {
        if (EnsureCountTokens(ResolveEncodingName(model)) is not { } countTokens)
        {
            return null;
        }

        return string.IsNullOrEmpty(text) ? 0 : countTokens(text);
    }

    public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var encodingName = ResolveEncodingName(model);
        if (EnsureCountTokens(encodingName) is not { } countTokens)
        {
            return null;
        }

        var total = 0;
        foreach (var message in messages)
        {
            total += CountMessage(encodingName, message, countTokens);
        }

        return total;
    }

    public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model)
    {
        var encodingName = ResolveEncodingName(model);
        if (EnsureCountTokens(encodingName) is not { } countTokens)
        {
            return null;
        }

        if (tools is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            total += CountTool(encodingName, tool, countTokens);
        }

        return total;
    }

    private static Func<string, int> CreateTiktokenCounter(string encodingName)
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);
        return text => tokenizer.CountTokens(text);
    }

    /// <summary>
    /// An explicit model option wins, then the model family picks an encoding, then the default. The
    /// result is whitelisted because an unknown name would throw while loading the vocabulary.
    /// </summary>
    private string ResolveEncodingName(AiModelProfile? model)
    {
        if (TryReadEncodingOverride(model, out var configured))
        {
            foreach (var supported in SupportedEncodingNames)
            {
                if (string.Equals(supported, configured, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }

            WarnUnsupportedEncodingOnce(configured);
            return DefaultEncodingName;
        }

        var modelName = model?.Model;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return DefaultEncodingName;
        }

        var normalized = modelName.Trim().ToLowerInvariant();
        if (normalized.Contains("gpt-4o", StringComparison.Ordinal) ||
            normalized.StartsWith("o1", StringComparison.Ordinal) ||
            normalized.StartsWith("o3", StringComparison.Ordinal))
        {
            return DefaultEncodingName;
        }

        return normalized.StartsWith("gpt-4", StringComparison.Ordinal) ||
               normalized.StartsWith("gpt-3.5", StringComparison.Ordinal)
            ? Cl100kBaseEncodingName
            : DefaultEncodingName;
    }

    private static bool TryReadEncodingOverride(AiModelProfile? model, out string configured)
    {
        configured = string.Empty;
        if (model is null ||
            !model.ModelOptions.TryGetValue(AiChatOptions.TokenizerEncodingKey, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        configured = value.Trim();
        return true;
    }

    private void WarnUnsupportedEncodingOnce(string configured)
    {
        lock (_gate)
        {
            if (!_warnedUnsupportedEncodings.Add(configured))
            {
                return;
            }
        }

        _logger.LogWarning(
            "Tokenizer encoding '{Encoding}' is not supported; falling back to {DefaultEncoding}.",
            configured,
            DefaultEncodingName);
    }

    /// <summary>
    /// Loads each vocabulary on first use of its encoding: the caller runs on the turn's thread, so the
    /// one-off decompression never blocks the UI. A failure only disables that encoding for the process.
    /// </summary>
    private Func<string, int>? EnsureCountTokens(string encodingName)
    {
        lock (_gate)
        {
            if (_tokenizers.TryGetValue(encodingName, out var created))
            {
                return created;
            }

            if (_failedEncodings.Contains(encodingName))
            {
                return null;
            }

            try
            {
                var countTokens = _tokenizerFactory(encodingName);
                _tokenizers[encodingName] = countTokens;
                return countTokens;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failedEncodings.Add(encodingName);
                _logger.LogWarning(
                    exception,
                    "Direct token counting is unavailable for encoding {Encoding} ({ExceptionType}: {ExceptionMessage}); " +
                    "token-based decisions are skipped for models using it.",
                    encodingName,
                    exception.GetType().Name,
                    exception.Message);
                return null;
            }
        }
    }

    private int CountMessage(string encodingName, ChatMessage message, Func<string, int> countTokens)
    {
        var parts = CollectMessageParts(message, out var imageCount);
        var key = CreateCacheKey(encodingName, message.MessageId, parts, imageCount);
        if (TryGetCachedCount(_messageCounts, key, out var cached))
        {
            return cached;
        }

        var tokens = PerMessageFramingTokens + parts.Sum(countTokens) + (imageCount * PerImageTokens);
        StoreCachedCount(_messageCounts, _messageCountOrder, key, tokens);
        return tokens;
    }

    private int CountTool(string encodingName, AITool tool, Func<string, int> countTokens)
    {
        var parts = CollectToolParts(tool);
        var key = CreateCacheKey(encodingName, tool.Name, parts);
        if (TryGetCachedCount(_toolCounts, key, out var cached))
        {
            return cached;
        }

        var tokens = PerToolFramingTokens + parts.Sum(countTokens);
        StoreCachedCount(_toolCounts, _toolCountOrder, key, tokens);
        return tokens;
    }

    private static List<string> CollectMessageParts(ChatMessage message, out int imageCount)
    {
        var parts = new List<string>();
        imageCount = 0;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    AddPart(parts, text.Text);
                    break;
                case DataContent:
                    // Binary content has no text to tokenize. Every DataContent Direct sends is an image.
                    imageCount++;
                    break;
                case FunctionCallContent call:
                    AddPart(parts, call.CallId);
                    AddPart(parts, call.Name);
                    AddPart(parts, Serialize(call.Arguments));
                    break;
                case FunctionResultContent result:
                    AddPart(parts, result.CallId);
                    CollectResultParts(result.Result, parts, ref imageCount);
                    AddPart(parts, result.Exception?.Message);
                    break;
            }
        }

        return parts;
    }

    /// <summary>
    /// A tool result may carry content parts on the native delivery path. An image is counted by the constant
    /// per-image cost and its bytes are never touched: serializing them would turn one image into megabytes of
    /// base64 in the tokenizer and in the cache key.
    /// </summary>
    private static void CollectResultParts(object? value, List<string> parts, ref int imageCount)
    {
        switch (value)
        {
            case AIContent content:
                CollectContentParts(content, parts, ref imageCount);
                break;
            case IEnumerable<AIContent> contents:
                foreach (var content in contents)
                {
                    CollectContentParts(content, parts, ref imageCount);
                }

                break;
            default:
                AddPart(parts, Serialize(value));
                break;
        }
    }

    private static void CollectContentParts(AIContent content, List<string> parts, ref int imageCount)
    {
        switch (content)
        {
            case TextContent text:
                AddPart(parts, text.Text);
                break;
            case DataContent:
                imageCount++;
                break;
            default:
                AddPart(parts, content.GetType().Name);
                break;
        }
    }

    private static List<string> CollectToolParts(AITool tool)
    {
        var parts = new List<string>();
        AddPart(parts, tool.Name);
        AddPart(parts, tool.Description);
        if (tool is AIFunctionDeclaration function && function.JsonSchema.ValueKind != JsonValueKind.Undefined)
        {
            AddPart(parts, function.JsonSchema.GetRawText());
        }

        if (tool.AdditionalProperties.Count > 0)
        {
            AddPart(parts, Serialize(tool.AdditionalProperties));
        }

        return parts;
    }

    /// <summary>
    /// The key carries the encoding name and the content fingerprint: the same text counts differently
    /// per vocabulary, and message adjustments rewrite content under an unchanged message id.
    /// </summary>
    private static string CreateCacheKey(string encodingName, string? identity, List<string> parts, int imageCount = 0)
    {
        var builder = new StringBuilder();
        // The image count is part of the fingerprint: bytes are not in the parts, so two messages with the
        // same text but a different number of images would otherwise share one cached count.
        builder.Append("\u001fimages:").Append(imageCount).Append('\u001f');
        foreach (var part in parts)
        {
            builder.Append(part).Append('\u001f');
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return string.Concat(encodingName, "\u001d", identity ?? string.Empty, "\u001e", fingerprint);
    }

    /// <summary>
    /// A string part is already the text the provider receives; serializing it would only add quotes and
    /// let the encoder rewrite it. Everything else (arguments, results, schema) is measured as JSON.
    /// </summary>
    private static string? Serialize(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(value, SerializerOptions)
    };

    private static void AddPart(List<string> parts, string? part)
    {
        if (!string.IsNullOrEmpty(part))
        {
            parts.Add(part);
        }
    }

    private bool TryGetCachedCount(Dictionary<string, int> cache, string key, out int count)
    {
        lock (_gate)
        {
            return cache.TryGetValue(key, out count);
        }
    }

    private void StoreCachedCount(Dictionary<string, int> cache, Queue<string> insertionOrder, string key, int count)
    {
        lock (_gate)
        {
            if (cache.ContainsKey(key))
            {
                return;
            }

            while (cache.Count >= MaximumCachedCounts && insertionOrder.Count > 0)
            {
                cache.Remove(insertionOrder.Dequeue());
            }

            cache[key] = count;
            insertionOrder.Enqueue(key);
        }
    }
}
