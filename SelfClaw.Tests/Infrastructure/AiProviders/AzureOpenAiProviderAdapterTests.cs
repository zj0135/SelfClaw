using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Azure;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AzureOpenAiProviderAdapterTests
{
    [Fact]
    public void Adapter_creates_chat_client_for_deployment_and_maps_options()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new StubHandler());
        var adapter = new AzureOpenAiProviderAdapter(httpClientProvider);
        var tool = AIFunctionFactory.Create(() => "ok", "test_tool");
        var request = CreateRequest(
            CreateConnection(JsonObject("{\"api-version\":\"2024-10-21\"}")),
            AiProviderApiFormat.OpenAIChatCompletions,
            new Dictionary<string, string> { ["api_key"] = "azure-key" },
            [tool]);

        adapter.ProviderKind.Should().Be(AiProviderKind.AzureOpenAI);
        adapter.SupportsModelListing.Should().BeFalse();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIResponses).Should().BeFalse();

        using var client = adapter.CreateChatClient(request);
        client.Should().NotBeNull();
        httpClientProvider.CachedClientCount.Should().Be(1);

        var options = adapter.CreateChatOptions(request);
        options.Temperature.Should().BeApproximately(0.25f, 0.0001f);
        options.TopP.Should().BeApproximately(0.8f, 0.0001f);
        options.ToolMode.Should().Be(ChatToolMode.Auto);
        options.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
    }

    [Fact]
    public async Task ListModelsAsync_explains_that_deployments_are_manual()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new StubHandler());
        var adapter = new AzureOpenAiProviderAdapter(httpClientProvider);

        var act = () => adapter.ListModelsAsync(CreateConnection(), new Dictionary<string, string>());

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*deployment*manually*");
    }

    [Fact]
    public void CreateChatClient_requires_api_key()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new StubHandler());
        var adapter = new AzureOpenAiProviderAdapter(httpClientProvider);
        var request = CreateRequest(
            CreateConnection(),
            AiProviderApiFormat.OpenAIChatCompletions,
            new Dictionary<string, string>());

        var act = () => adapter.CreateChatClient(request);

        act.Should().Throw<InvalidOperationException>().WithMessage("*api_key*");
        httpClientProvider.CachedClientCount.Should().Be(0);
    }

    [Fact]
    public void Unsupported_format_and_api_version_fail_with_readable_errors()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new StubHandler());
        var adapter = new AzureOpenAiProviderAdapter(httpClientProvider);
        var connection = CreateConnection(JsonObject("{\"api-version\":\"2099-01-01\"}"));
        var unsupportedFormat = CreateRequest(
            connection,
            AiProviderApiFormat.OpenAIResponses,
            new Dictionary<string, string> { ["api_key"] = "azure-key" });
        var unsupportedVersion = CreateRequest(
            connection,
            AiProviderApiFormat.OpenAIChatCompletions,
            new Dictionary<string, string> { ["api_key"] = "azure-key" });

        var formatAct = () => adapter.CreateChatClient(unsupportedFormat);
        var versionAct = () => adapter.CreateChatClient(unsupportedVersion);

        formatAct.Should().Throw<NotSupportedException>().WithMessage("*OpenAIResponses*Test deployment*");
        versionAct.Should().Throw<InvalidOperationException>().WithMessage("*2099-01-01*Azure SDK*");
    }

    private static AiProviderConnection CreateConnection(
        IReadOnlyDictionary<string, JsonElement>? connectionOptions = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            "azure-openai",
            "Azure OpenAI",
            AiProviderKind.AzureOpenAI,
            new Uri("https://selfclaw.openai.azure.com/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { ["api_key"] = "secret:azure" },
            connectionOptions ?? JsonObject("{}"),
            now,
            now);
    }

    private static AiProviderClientRequest CreateRequest(
        AiProviderConnection connection,
        AiProviderApiFormat apiFormat,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlyList<AITool>? tools = null)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test deployment",
            apiFormat,
            "gpt-4o-production",
            new AiSamplingOptions(true, 0.25, true, 0.8),
            JsonObject("{}"),
            now,
            now);
        return new AiProviderClientRequest(connection, profile, secrets, false, tools ?? []);
    }

    private static IReadOnlyDictionary<string, JsonElement> JsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No network request was expected.");
    }
}
