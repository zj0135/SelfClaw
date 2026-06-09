using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class OpenAiProviderAdapterTests
{
    [Fact]
    public void SupportsApiFormat_returns_true_for_chat_completions_and_responses()
    {
        var adapter = new OpenAiProviderAdapter();

        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIResponses).Should().BeTrue();
        adapter.SupportsApiFormat((AiProviderApiFormat)999).Should().BeFalse();
    }

    [Fact]
    public void CreateChatClient_creates_chat_completions_client()
    {
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAICompatible);
        var request = CreateRequest(
            AiProviderKind.OpenAICompatible,
            AiProviderApiFormat.OpenAIChatCompletions);

        var client = adapter.CreateChatClient(request);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateChatClient_creates_responses_client()
    {
        var adapter = new OpenAiProviderAdapter();
        var request = CreateRequest(AiProviderKind.OpenAI, AiProviderApiFormat.OpenAIResponses);

        var client = adapter.CreateChatClient(request);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateChatOptions_maps_sampling_parameters()
    {
        var adapter = new OpenAiProviderAdapter();
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIResponses,
            sampling: new AiSamplingOptions(true, 0.25, true, 0.8));

        var options = adapter.CreateChatOptions(request);

        options.Temperature.Should().Be(0.25f);
        options.TopP.Should().Be(0.8f);
    }

    [Fact]
    public void CreateChatOptions_maps_tool_mode()
    {
        var adapter = new OpenAiProviderAdapter();
        var withoutTools = adapter.CreateChatOptions(CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIResponses));
        var withTools = adapter.CreateChatOptions(CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIResponses,
            tools: [CreateTool()]));

        withoutTools.ToolMode.Should().Be(ChatToolMode.None);
        withoutTools.Tools.Should().BeNull();
        withTools.ToolMode.Should().Be(ChatToolMode.Auto);
        withTools.Tools.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, "enabled")]
    [InlineData(false, "disabled")]
    public void ChatCompletions_options_apply_reasoning_default_for_openai_compatible(
        bool enableReasoning,
        string expectedThinkingType)
    {
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAICompatible);
        var request = CreateRequest(
            AiProviderKind.OpenAICompatible,
            AiProviderApiFormat.OpenAIChatCompletions,
            enableReasoning: enableReasoning);

        var rawJson = ReadRawOptionsJson(adapter.CreateChatOptions(request));

        rawJson.GetProperty("thinking").GetProperty("type").GetString().Should().Be(expectedThinkingType);
    }

    [Fact]
    public void ChatCompletions_options_do_not_apply_reasoning_default_for_strict_openai()
    {
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAI);
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIChatCompletions,
            enableReasoning: true);

        var rawJson = ReadRawOptionsJson(adapter.CreateChatOptions(request));

        rawJson.TryGetProperty("thinking", out _).Should().BeFalse();
    }

    [Fact]
    public void ChatCompletions_options_apply_model_specific_options()
    {
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAI);
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIChatCompletions,
            modelOptions: ReadJsonObject("""
            {
              "thinking.type": "enabled",
              "reasoning_effort": "high",
              "parallel_tool_calls": true,
              "store": false
            }
            """));

        var rawJson = ReadRawOptionsJson(adapter.CreateChatOptions(request));

        rawJson.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        rawJson.GetProperty("reasoning_effort").GetString().Should().Be("high");
        rawJson.GetProperty("parallel_tool_calls").GetBoolean().Should().BeTrue();
        rawJson.GetProperty("store").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Responses_options_apply_model_specific_options()
    {
        var adapter = new OpenAiProviderAdapter();
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIResponses,
            enableReasoning: true,
            modelOptions: ReadJsonObject("""
            {
              "reasoning.effort": "medium",
              "reasoning.summary": "auto",
              "store": true,
              "max_output_tokens": 2048,
              "truncation": "auto",
              "parallel_tool_calls": false
            }
            """));

        var rawJson = ReadRawOptionsJson(adapter.CreateChatOptions(request));

        rawJson.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("medium");
        rawJson.GetProperty("reasoning").GetProperty("summary").GetString().Should().Be("auto");
        rawJson.GetProperty("store").GetBoolean().Should().BeTrue();
        rawJson.GetProperty("max_output_tokens").GetInt32().Should().Be(2048);
        rawJson.GetProperty("truncation").GetString().Should().Be("auto");
        rawJson.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Responses_options_do_not_apply_reasoning_from_enable_reasoning()
    {
        var adapter = new OpenAiProviderAdapter();
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            AiProviderApiFormat.OpenAIResponses,
            enableReasoning: true);

        var rawJson = ReadRawOptionsJson(adapter.CreateChatOptions(request));

        rawJson.TryGetProperty("reasoning", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateChatOptions_throws_for_unsupported_api_format()
    {
        var adapter = new OpenAiProviderAdapter();
        var request = CreateRequest(
            AiProviderKind.OpenAI,
            (AiProviderApiFormat)999);

        var act = () => adapter.CreateChatOptions(request);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("*OpenAI*999*Test profile*");
    }

    private static AiProviderClientRequest CreateRequest(
        AiProviderKind providerKind,
        AiProviderApiFormat apiFormat,
        bool enableReasoning = false,
        IReadOnlyDictionary<string, JsonElement>? modelOptions = null,
        IReadOnlyList<AITool>? tools = null,
        AiSamplingOptions? sampling = null)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            providerKind.ToString(),
            providerKind,
            new Uri("https://api.example.test/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                [OpenAiProviderAdapter.ApiKeySecretName] = "secret:test"
            },
            ReadJsonObject("{}"),
            now,
            now);
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test profile",
            apiFormat,
            "test-model",
            sampling ?? new AiSamplingOptions(false, 0.7, false, 0.7),
            modelOptions ?? ReadJsonObject("{}"),
            null,
            null,
            now,
            now);

        return new AiProviderClientRequest(
            connection,
            profile,
            new Dictionary<string, string>
            {
                [OpenAiProviderAdapter.ApiKeySecretName] = "test-api-key"
            },
            enableReasoning,
            tools ?? []);
    }

    private static AITool CreateTool() =>
        AIFunctionFactory.Create(
            (Func<string>)(() => "ok"),
            new AIFunctionFactoryOptions
            {
                Name = "test_tool",
                Description = "A test tool."
            });

    private static JsonElement ReadRawOptionsJson(ChatOptions options)
    {
        options.RawRepresentationFactory.Should().NotBeNull();
        var rawOptions = options.RawRepresentationFactory!(null!)!;
        var json = ModelReaderWriter.Write(rawOptions, ModelReaderWriterOptions.Json).ToString();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
}
