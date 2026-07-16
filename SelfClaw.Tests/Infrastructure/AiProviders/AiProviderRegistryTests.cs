using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiProviderRegistryTests
{
    [Fact]
    public void GetRequiredAdapter_returns_registered_adapter()
    {
        var openAiAdapter = new FakeAiProviderAdapter(AiProviderKind.OpenAI);
        var compatibleAdapter = new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible);
        var registry = new AiProviderRegistry([openAiAdapter, compatibleAdapter]);

        registry.GetRequiredAdapter(AiProviderKind.OpenAI).Should().BeSameAs(openAiAdapter);
        registry.GetRequiredAdapter(AiProviderKind.OpenAICompatible).Should().BeSameAs(compatibleAdapter);
    }

    [Fact]
    public void GetRequiredAdapter_throws_when_provider_is_not_registered()
    {
        var registry = new AiProviderRegistry([new FakeAiProviderAdapter(AiProviderKind.OpenAI)]);

        var act = () => registry.GetRequiredAdapter(AiProviderKind.GoogleGemini);

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage("*GoogleGemini*");
    }

    [Fact]
    public void Constructor_throws_when_provider_kind_is_registered_twice()
    {
        var act = () => new AiProviderRegistry(
        [
            new FakeAiProviderAdapter(AiProviderKind.OpenAI),
            new FakeAiProviderAdapter(AiProviderKind.OpenAI)
        ]);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*OpenAI*");
    }

    private sealed class FakeAiProviderAdapter : IAiProviderAdapter
    {
        public FakeAiProviderAdapter(AiProviderKind providerKind)
        {
            ProviderKind = providerKind;
        }

        public AiProviderKind ProviderKind { get; }

        public bool SupportsApiFormat(AiProviderApiFormat apiFormat) => true;

        public bool SupportsModelListing => true;

        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
            AiProviderConnection connection,
            IReadOnlyDictionary<string, string> secrets,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiModelDescriptor>>([]);

        public IChatClient CreateChatClient(AiProviderClientRequest request)
            => throw new NotImplementedException();

        public ChatOptions CreateChatOptions(AiProviderClientRequest request)
            => throw new NotImplementedException();
    }
}
