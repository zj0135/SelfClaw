using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class DirectTokenCounterTests
{
    private static readonly AiModelProfile O200kModel = CreateProfile("gpt-4o-mini");
    private static readonly AiModelProfile Cl100kModel = CreateProfile("gpt-4-turbo");

    [Theory]
    [InlineData("cl100k_base", "cl100k_base")]
    [InlineData("CL100K_BASE", "cl100k_base")]
    public void Explicit_encoding_override_wins_over_the_model_name_mapping(string overrideName, string expectedEncoding)
    {
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(tokenizerFactory: factory.Create);

        counter.CountText("hello", CreateProfile("gpt-4o-mini", overrideName)).Should().Be(5);

        factory.Encodings.Should().Equal(expectedEncoding);
    }

    [Theory]
    [InlineData("gpt-4o-mini", "o200k_base")]
    [InlineData("o1-preview", "o200k_base")]
    [InlineData("o3-mini", "o200k_base")]
    [InlineData("gpt-4-turbo", "cl100k_base")]
    [InlineData("gpt-3.5-turbo", "cl100k_base")]
    [InlineData("claude-sonnet-4-20250514", "o200k_base")]
    [InlineData("", "o200k_base")]
    public void Model_name_selects_the_encoding_of_its_openai_wire_family(string model, string expectedEncoding)
    {
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(tokenizerFactory: factory.Create);

        counter.CountText("hello", CreateProfile(model)).Should().Be(5);

        factory.Encodings.Should().Equal(expectedEncoding);
    }

    [Fact]
    public void Unknown_encoding_override_falls_back_to_the_default_and_warns_once()
    {
        var logger = new CapturingLogger();
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(logger, factory.Create);
        var profile = CreateProfile("gpt-4-turbo", "p50k_base");

        counter.CountText("hello", profile).Should().Be(5);
        counter.CountText("world", profile).Should().Be(5);

        factory.Encodings.Should().Equal(DirectTokenCounter.DefaultEncodingName);
        logger.Messages.Should().ContainSingle().Which.Should().Contain("p50k_base");
    }

    [Fact]
    public void One_instance_counts_both_encodings_per_call()
    {
        var encodings = new List<string>();
        var counter = new DirectTokenCounter(tokenizerFactory: encodingName =>
        {
            lock (encodings)
            {
                encodings.Add(encodingName);
            }

            return RealTokenizer(encodingName);
        });

        counter.CountText("你好，世界", O200kModel).Should().Be(3);
        counter.CountText("你好，世界", Cl100kModel).Should().Be(6);

        encodings.Should().BeEquivalentTo([DirectTokenCounter.DefaultEncodingName, "cl100k_base"]);
        encodings.Should().HaveCount(2);
    }

    [Fact]
    public void Counts_match_the_offline_vocabulary_for_ascii_cjk_and_emoji()
    {
        var counter = new DirectTokenCounter();
        var samples = new (string Text, int Expected)[]
        {
            ("Hello, world!", 4),
            ("The quick brown fox jumps over the lazy dog.", 10),
            ("tokenizer", 2),
            ("你好，世界", 3),
            ("🙂🚀", 3),
            ("混合 mixed 文本 with 🙂 emoji", 8)
        };

        foreach (var (text, expected) in samples)
        {
            counter.CountText(text, O200kModel).Should().Be(expected, "the tokenizer must count '{0}' deterministically", text);
            counter.CountText(text, O200kModel).Should().Be(expected);
        }

        // The retired byte heuristic reported 5 for both samples below, so these values pin the
        // tokenizer path rather than an estimate that happens to look similar.
        counter.CountText("Hello, world!", O200kModel).Should().NotBe((Encoding.UTF8.GetByteCount("Hello, world!") + 2) / 3);
        counter.CountText("你好，世界", O200kModel).Should().NotBe((Encoding.UTF8.GetByteCount("你好，世界") + 2) / 3);
    }

    [Fact]
    public void Unknown_model_falls_back_to_the_default_encoding()
    {
        var counter = new DirectTokenCounter();

        counter.CountText("你好，世界", null).Should().Be(3);
        counter.CountText("你好，世界", CreateProfile("some-local-model")).Should().Be(3);
    }

    [Fact]
    public void Cl100k_models_load_their_own_embedded_vocabulary()
    {
        var counter = new DirectTokenCounter();

        counter.IsAvailable(Cl100kModel).Should().BeTrue();
        counter.CountText("你好，世界", Cl100kModel).Should().Be(6);
        counter.CountText("🙂🚀", Cl100kModel).Should().Be(5);
    }

    [Fact]
    public void Empty_message_costs_only_the_per_message_framing()
    {
        var counter = new DirectTokenCounter();

        counter.CountMessages([new ChatMessage(ChatRole.User, "")], O200kModel)
            .Should().Be(DirectTokenCounter.PerMessageFramingTokens);
        counter.CountMessages([new ChatMessage(ChatRole.Assistant, new List<AIContent>())], O200kModel)
            .Should().Be(DirectTokenCounter.PerMessageFramingTokens);
        counter.CountMessages([], O200kModel).Should().Be(0);
    }

    [Fact]
    public void Message_counts_include_framing_and_every_content_part()
    {
        var counter = new DirectTokenCounter();
        var message = new ChatMessage(ChatRole.Assistant, "Done.")
        {
            Contents =
            {
                new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = "value" })
            }
        };

        var textOnly = Count(counter, [new ChatMessage(ChatRole.Assistant, "Done.")]);
        var withCall = Count(counter, [message]);

        textOnly.Should().Be(DirectTokenCounter.PerMessageFramingTokens + Count(counter, "Done."));
        withCall.Should().BeGreaterThan(textOnly + Count(counter, "call-1") + Count(counter, "lookup"));
    }

    [Fact]
    public void Function_result_text_is_counted_as_the_provider_receives_it()
    {
        var counter = new DirectTokenCounter();
        const string result = "你好，世界";
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)]);

        // The provider receives the raw text. Serializing the string into JSON first would add quotes and let
        // the default encoder rewrite every CJK character into a six-character \uXXXX escape.
        var raw = DirectTokenCounter.PerMessageFramingTokens + Count(counter, "call-1") + Count(counter, result);
        var escaped = DirectTokenCounter.PerMessageFramingTokens + Count(counter, "call-1") +
                      Count(counter, "\"\\u4F60\\u597D\\uFF0C\\u4E16\\u754C\"");

        Count(counter, [message]).Should().Be(raw).And.BeLessThan(escaped);
    }

    [Fact]
    public void Html_sensitive_and_generic_brackets_in_tool_results_are_not_escaped()
    {
        var counter = new DirectTokenCounter();
        const string result = "<div class=\"x\">List<string> & co</div>";
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)]);

        var expected = DirectTokenCounter.PerMessageFramingTokens + Count(counter, "call-1") + Count(counter, result);

        Count(counter, [message]).Should().Be(expected);
    }

    [Fact]
    public void A_structured_tool_result_is_still_measured_as_json()
    {
        var counter = new DirectTokenCounter();
        var payload = JsonSerializer.SerializeToElement(new { path = "README.md", ok = true });
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", payload)]);

        var expected = DirectTokenCounter.PerMessageFramingTokens + Count(counter, "call-1") +
                       Count(counter, payload.GetRawText());

        Count(counter, [message]).Should().Be(expected);
    }

    [Fact]
    public void Tool_counts_include_the_schema_text_and_per_tool_framing()
    {
        var counter = new DirectTokenCounter();
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } }
        });
        var tool = AIFunctionFactory.CreateDeclaration("lookup", "Lookup a value.", schema);

        var expected = DirectTokenCounter.PerToolFramingTokens +
                       Count(counter, tool.Name) +
                       Count(counter, tool.Description) +
                       Count(counter, schema.GetRawText());
        counter.CountTools([tool], O200kModel).Should().Be(expected);

        var larger = AIFunctionFactory.CreateDeclaration("lookup", new string('d', 400), schema);
        counter.CountTools([larger], O200kModel).Should().BeGreaterThan(expected);
        counter.CountTools(null, O200kModel).Should().Be(0);
    }

    [Fact]
    public void Message_counts_are_cached_per_message_id_and_content()
    {
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(tokenizerFactory: factory.Create);
        var message = new ChatMessage(ChatRole.User, "hello") { MessageId = "message-1" };

        var first = counter.CountMessages([message], O200kModel);
        var second = counter.CountMessages([message], O200kModel);

        first.Should().Be(second).And.Be(9);
        factory.EncodeCalls.Should().Be(1);

        // Adjustments rewrite content without changing the message id; a stale hit would count the old text.
        var rewritten = new ChatMessage(ChatRole.User, "hello, rewritten") { MessageId = "message-1" };
        counter.CountMessages([rewritten], O200kModel).Should().Be(20);
        factory.EncodeCalls.Should().Be(2);
    }

    [Fact]
    public void Message_cache_entries_are_scoped_by_encoding()
    {
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(tokenizerFactory: factory.Create);
        var message = new ChatMessage(ChatRole.User, "hello") { MessageId = "message-1" };

        counter.CountMessages([message], O200kModel).Should().Be(9);
        factory.EncodeCalls.Should().Be(1);

        // Same message, different vocabulary: the o200k entry must not answer for cl100k.
        counter.CountMessages([message], Cl100kModel).Should().Be(9);
        factory.EncodeCalls.Should().Be(2);
        counter.CountMessages([message], Cl100kModel).Should().Be(9);
        factory.EncodeCalls.Should().Be(2);
    }

    [Fact]
    public void Message_count_cache_evicts_the_oldest_entry_past_its_cap()
    {
        var factory = new RecordingTokenizerFactory();
        var counter = new DirectTokenCounter(tokenizerFactory: factory.Create);
        var messages = Enumerable.Range(0, 4100)
            .Select(index => new ChatMessage(ChatRole.User, $"message {index}") { MessageId = $"message-{index}" })
            .ToArray();

        foreach (var message in messages)
        {
            counter.CountMessages([message], O200kModel);
        }

        factory.EncodeCalls.Should().Be(messages.Length);

        // The cap is 4096, so the four oldest entries are gone and the first message is counted again.
        counter.CountMessages([messages[0]], O200kModel);
        factory.EncodeCalls.Should().Be(messages.Length + 1);
        counter.CountMessages([messages[^1]], O200kModel);
        factory.EncodeCalls.Should().Be(messages.Length + 1);
    }

    [Fact]
    public void Failed_encoding_degrades_alone_and_warns_once()
    {
        var logger = new CapturingLogger();
        var counter = new DirectTokenCounter(
            logger,
            encodingName => encodingName == "cl100k_base"
                ? throw new InvalidOperationException("cl100k vocabulary is missing")
                : RealTokenizer(encodingName));

        counter.IsAvailable(Cl100kModel).Should().BeFalse();
        counter.CountText("hello", Cl100kModel).Should().BeNull();
        counter.CountMessages([new ChatMessage(ChatRole.User, "hello")], Cl100kModel).Should().BeNull();
        counter.CountTools([AIFunctionFactory.CreateDeclaration("lookup", "Lookup a value.", JsonSerializer.SerializeToElement(new { type = "object" }))], Cl100kModel)
            .Should().BeNull();
        counter.IsAvailable(Cl100kModel).Should().BeFalse();

        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain(nameof(InvalidOperationException))
            .And.Contain("cl100k vocabulary is missing");

        counter.IsAvailable(O200kModel).Should().BeTrue();
        counter.CountText("你好，世界", O200kModel).Should().Be(3);
    }

    [Fact]
    public void Concurrent_counting_is_consistent_across_encodings()
    {
        var creationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var counter = new DirectTokenCounter(tokenizerFactory: encodingName =>
        {
            lock (creationCounts)
            {
                creationCounts[encodingName] = creationCounts.GetValueOrDefault(encodingName) + 1;
            }

            return RealTokenizer(encodingName);
        });

        var results = new int?[64];
        Parallel.For(0, results.Length, index => results[index] = index % 2 == 0
            ? counter.CountText("你好，世界", O200kModel)
            : counter.CountText("你好，世界", Cl100kModel));

        for (var index = 0; index < results.Length; index++)
        {
            results[index].Should().Be(index % 2 == 0 ? 3 : 6);
        }

        creationCounts.Should().HaveCount(2);
        creationCounts[DirectTokenCounter.DefaultEncodingName].Should().Be(1);
        creationCounts["cl100k_base"].Should().Be(1);
    }

    private static Func<string, int> RealTokenizer(string encodingName)
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);
        return text => tokenizer.CountTokens(text);
    }

    private static int Count(DirectTokenCounter counter, string? text)
        => counter.CountText(text, O200kModel) ?? throw new InvalidOperationException("Token counting is unavailable.");

    private static int Count(DirectTokenCounter counter, IReadOnlyList<ChatMessage> messages)
        => counter.CountMessages(messages, O200kModel) ?? throw new InvalidOperationException("Token counting is unavailable.");

    private static AiModelProfile CreateProfile(string model, string? encodingOverride = null)
    {
        var options = new Dictionary<string, JsonElement>();
        if (encodingOverride is not null)
        {
            options[AiChatOptions.TokenizerEncodingKey] = JsonSerializer.SerializeToElement(encodingOverride);
        }

        return new AiModelProfile(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            AiProviderApiFormat.OpenAIChatCompletions,
            model,
            new AiSamplingOptions(false, 0d, false, 0d),
            options,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingTokenizerFactory
    {
        internal List<string> Encodings { get; } = [];

        internal int EncodeCalls { get; private set; }

        internal Func<string, int> Create(string encodingName)
        {
            Encodings.Add(encodingName);
            return text =>
            {
                EncodeCalls++;
                return text.Length;
            };
        }
    }

    private sealed class CapturingLogger : ILogger<DirectTokenCounter>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
