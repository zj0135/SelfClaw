using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AnthropicProviderAdapterTests
{
    [Fact]
    public void SupportsApiFormat_returns_true_only_for_anthropic_messages()
    {
        var adapter = new AnthropicProviderAdapter();

        adapter.SupportsApiFormat(AiProviderApiFormat.AnthropicMessages).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeFalse();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIResponses).Should().BeFalse();
    }

    [Fact]
    public void CreateChatClient_creates_anthropic_chat_client()
    {
        var adapter = new AnthropicProviderAdapter();
        var request = CreateRequest();

        var client = adapter.CreateChatClient(request);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateChatOptions_maps_sampling_tools_and_max_tokens()
    {
        var adapter = new AnthropicProviderAdapter();
        var request = CreateRequest(
            sampling: new AiSamplingOptions(true, 0.3, true, 0.95),
            modelOptions: ReadJsonObject("{\"max_tokens\":4096}"),
            tools: [CreateTool()]);

        var options = adapter.CreateChatOptions(request);

        options.Temperature.Should().Be(0.3f);
        options.TopP.Should().Be(0.95f);
        options.MaxOutputTokens.Should().Be(4096);
        options.ToolMode.Should().Be(ChatToolMode.Auto);
        options.Tools.Should().ContainSingle();
    }

    [Fact]
    public void CreateChatOptions_uses_none_tool_mode_without_tools()
    {
        var adapter = new AnthropicProviderAdapter();
        var request = CreateRequest();

        var options = adapter.CreateChatOptions(request);

        options.ToolMode.Should().Be(ChatToolMode.None);
        options.Tools.Should().BeNull();
    }

    [Fact]
    public void CreateChatOptions_throws_for_unsupported_api_format()
    {
        var adapter = new AnthropicProviderAdapter();
        var request = CreateRequest(apiFormat: AiProviderApiFormat.OpenAIResponses);

        var act = () => adapter.CreateChatOptions(request);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("*Anthropic*OpenAIResponses*Test profile*");
    }

    [Fact]
    public void CreateChatClient_throws_when_api_key_is_missing()
    {
        var adapter = new AnthropicProviderAdapter();
        var request = CreateRequest(secrets: new Dictionary<string, string>());

        var act = () => adapter.CreateChatClient(request);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*api_key*");
    }

    private static AiProviderClientRequest CreateRequest(
        AiProviderApiFormat apiFormat = AiProviderApiFormat.AnthropicMessages,
        IReadOnlyDictionary<string, string>? secrets = null,
        IReadOnlyDictionary<string, JsonElement>? modelOptions = null,
        IReadOnlyList<AITool>? tools = null,
        AiSamplingOptions? sampling = null)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            "Anthropic",
            AiProviderKind.Anthropic,
            new Uri("https://api.anthropic.com/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                [AnthropicProviderAdapter.ApiKeySecretName] = "secret:anthropic"
            },
            ReadJsonObject("{}"),
            now,
            now);
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test profile",
            apiFormat,
            "claude-sonnet-4-5",
            sampling ?? new AiSamplingOptions(false, 0.7, false, 0.7),
            modelOptions ?? ReadJsonObject("{}"),
            now,
            now);

        return new AiProviderClientRequest(
            connection,
            profile,
            secrets ?? new Dictionary<string, string>
            {
                [AnthropicProviderAdapter.ApiKeySecretName] = "test-api-key"
            },
            EnableReasoning: false,
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

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
}
