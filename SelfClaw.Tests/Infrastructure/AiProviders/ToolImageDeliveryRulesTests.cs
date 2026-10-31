using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class ToolImageDeliveryRulesTests
{
    private const string NotMultimodalReason =
        "This model is not marked as multimodal. Enable multimodal support for it in AI provider settings.";

    [Theory]
    [InlineData(AiProviderApiFormat.AnthropicMessages, true)]
    [InlineData(AiProviderApiFormat.AnthropicMessages, false)]
    [InlineData(AiProviderApiFormat.AnthropicMessages, null)]
    [InlineData(AiProviderApiFormat.OpenAIResponses, true)]
    [InlineData(AiProviderApiFormat.OpenAIResponses, false)]
    [InlineData(AiProviderApiFormat.OpenAIResponses, null)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, true)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, false)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, null)]
    public void Resolve_follows_the_gate_order(AiProviderApiFormat apiFormat, bool? isMultimodal)
    {
        var capability = ToolImageDeliveryRules.Resolve(Profile(apiFormat, isMultimodal));

        var (expectedDelivery, expectedReason) = Expect(apiFormat, isMultimodal);
        capability.Delivery.Should().Be(expectedDelivery);
        capability.UnavailableReason.Should().Be(expectedReason);
    }

    [Fact]
    public void An_unverified_api_format_stays_unsupported()
    {
        var capability = ToolImageDeliveryRules.Resolve(Profile((AiProviderApiFormat)99, true));

        capability.Delivery.Should().Be(ToolImageDelivery.Unsupported);
        capability.UnavailableReason.Should().Be("API format '99' is not supported for image tool results.");
    }

    [Fact]
    public void A_multimodal_model_is_supported_without_any_extra_option()
    {
        // The gate has exactly two inputs: the model's own declaration and the wire format. A model that
        // declares multimodality must not need a second opt-in buried in model options.
        var profile = Profile(AiProviderApiFormat.AnthropicMessages, true) with
        {
            ModelOptions = new Dictionary<string, JsonElement>
            {
                ["image_tool_results"] = JsonSerializer.SerializeToElement(false)
            }
        };

        ToolImageDeliveryRules.Resolve(profile).Delivery.Should().Be(ToolImageDelivery.Native);
    }

    private static (ToolImageDelivery Delivery, string? Reason) Expect(
        AiProviderApiFormat apiFormat, bool? isMultimodal)
    {
        if (isMultimodal != true)
        {
            return (ToolImageDelivery.Unsupported, NotMultimodalReason);
        }

        return apiFormat switch
        {
            AiProviderApiFormat.AnthropicMessages => (ToolImageDelivery.Native, (string?)null),
            AiProviderApiFormat.OpenAIResponses => (ToolImageDelivery.Native, null),
            AiProviderApiFormat.OpenAIChatCompletions => (ToolImageDelivery.FollowUpUserMessage, null),
            _ => (ToolImageDelivery.Unsupported, $"API format '{apiFormat}' is not supported for image tool results.")
        };
    }

    private static AiModelProfile Profile(AiProviderApiFormat apiFormat, bool? isMultimodal)
    {
        var now = DateTimeOffset.UtcNow;
        var sampling = new AiSamplingOptions(false, 0, false, 0);
        return new AiModelProfile(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            apiFormat,
            "test-model",
            sampling,
            new Dictionary<string, JsonElement>(),
            now,
            now,
            Configuration: new AiModelConfiguration("test-model", "Test model", sampling, isMultimodal));
    }
}
