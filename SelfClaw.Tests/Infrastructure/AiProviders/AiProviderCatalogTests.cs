using SelfClaw.Core.Models;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.Catalog;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiProviderCatalogTests
{
    [Fact]
    public void Entries_define_the_three_unique_built_in_providers()
    {
        AiProviderCatalog.Entries.Should().HaveCount(3);
        AiProviderCatalog.Entries.Select(entry => entry.CatalogId)
            .Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(
                "openai",
                "anthropic",
                "custom");
    }

    [Fact]
    public void Entries_have_valid_protocol_and_display_metadata()
    {
        foreach (var entry in AiProviderCatalog.Entries)
        {
            entry.CatalogId.Should().NotBeNullOrWhiteSpace();
            entry.DisplayName.Should().NotBeNullOrWhiteSpace();
            entry.Subtitle.Should().NotBeNullOrWhiteSpace();
            entry.AccentColor.Should().MatchRegex("^#[0-9A-F]{6}$");
            entry.DefaultEndpoint.IsAbsoluteUri.Should().BeTrue();
            entry.SupportedFormats.Should().Contain(entry.DefaultApiFormat);
            entry.WellKnownModels.Should().BeEmpty();
        }
    }

    [Fact]
    public void Entries_require_an_api_key_for_every_built_in_provider()
    {
        foreach (var entry in AiProviderCatalog.Entries)
        {
            entry.AuthKind.Should().Be(AiProviderAuthKind.ApiKey);
        }

        var custom = AiProviderCatalog.GetRequired("custom");
        custom.SupportedFormats.Should().Contain(AiProviderApiFormat.OpenAIChatCompletions);
        custom.SupportedFormats.Should().Contain(AiProviderApiFormat.OpenAIResponses);
    }

    [Theory]
    [InlineData("openai", AiProviderKind.OpenAI, AiProviderApiFormat.OpenAIResponses, AiProviderAuthKind.ApiKey, true)]
    [InlineData("anthropic", AiProviderKind.Anthropic, AiProviderApiFormat.AnthropicMessages, AiProviderAuthKind.ApiKey, true)]
    [InlineData("custom", AiProviderKind.OpenAICompatible, AiProviderApiFormat.OpenAIChatCompletions, AiProviderAuthKind.ApiKey, true)]
    public void GetRequired_resolves_catalog_contract(
        string catalogId,
        AiProviderKind providerKind,
        AiProviderApiFormat defaultApiFormat,
        AiProviderAuthKind authKind,
        bool supportsModelListing)
    {
        var entry = AiProviderCatalog.GetRequired(catalogId);

        entry.CatalogId.Should().Be(catalogId);
        entry.ProviderKind.Should().Be(providerKind);
        entry.DefaultApiFormat.Should().Be(defaultApiFormat);
        entry.AuthKind.Should().Be(authKind);
        entry.SupportsModelListing.Should().Be(supportsModelListing);
    }

    [Theory]
    [InlineData("missing-provider")]
    [InlineData("")]
    [InlineData(null)]
    public void GetRequired_falls_back_to_custom_for_unknown_catalog_id(string? catalogId)
    {
        AiProviderCatalog.GetRequired(catalogId!).CatalogId.Should().Be("custom");
    }
}
