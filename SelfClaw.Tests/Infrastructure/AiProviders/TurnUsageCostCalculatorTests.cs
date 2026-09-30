using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class TurnUsageCostCalculatorTests
{
    [Fact]
    public void ComputeCostUsdMicros_prices_every_bucket_independently()
    {
        var configuration = Configuration(inputPrice: 3m, outputPrice: 15m, cacheReadPrice: 0.3m, cacheWritePrice: 3.75m);
        var usage = new TurnUsage(
            UncachedInputTokens: 1_000_000,
            CachedInputTokens: 2_000_000,
            CacheWriteInputTokens: 1_000_000,
            OutputTokens: 1_000_000);

        var cost = TurnUsageCostCalculator.ComputeCostUsdMicros(usage, configuration);

        // 3.00 + 0.60 + 3.75 + 15.00 = 22.35 USD.
        cost.Should().Be(22_350_000);
    }

    [Fact]
    public void ComputeCostUsdMicros_falls_back_to_the_input_price_for_missing_cache_prices()
    {
        var configuration = Configuration(inputPrice: 3m, outputPrice: 15m);
        var usage = new TurnUsage(CachedInputTokens: 1_000_000, CacheWriteInputTokens: 1_000_000, OutputTokens: 0);

        var cost = TurnUsageCostCalculator.ComputeCostUsdMicros(usage, configuration);

        cost.Should().Be(6_000_000);
    }

    [Fact]
    public void ComputeCostUsdMicros_returns_null_when_the_model_has_no_prices()
    {
        TurnUsageCostCalculator.ComputeCostUsdMicros(
                new TurnUsage(OutputTokens: 10),
                Configuration(null, null))
            .Should().BeNull();
        TurnUsageCostCalculator.ComputeCostUsdMicros(new TurnUsage(OutputTokens: 10), configuration: null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(3d, null)]
    [InlineData(null, 15d)]
    public void ComputeCostUsdMicros_returns_null_when_a_required_price_is_missing(double? inputPrice, double? outputPrice)
    {
        // A partially priced model must not bill the unpriced side at zero while claiming to be an estimate.
        TurnUsageCostCalculator.ComputeCostUsdMicros(
                new TurnUsage(UncachedInputTokens: 1_000_000, OutputTokens: 1_000_000),
                Configuration((decimal?)inputPrice, (decimal?)outputPrice))
            .Should().BeNull();
    }

    [Fact]
    public void ComputeCostUsdMicros_rounds_to_the_nearest_micro_dollar()
    {
        var configuration = Configuration(inputPrice: 1m, outputPrice: 1m);
        var usage = new TurnUsage(UncachedInputTokens: 1, OutputTokens: 0);

        var cost = TurnUsageCostCalculator.ComputeCostUsdMicros(usage, configuration);

        cost.Should().Be(1);
    }

    private static AiModelConfiguration Configuration(
        decimal? inputPrice,
        decimal? outputPrice,
        decimal? cacheReadPrice = null,
        decimal? cacheWritePrice = null)
        => new(
            "test-model",
            "Test Model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            PriceInPerMTok: inputPrice,
            PriceOutPerMTok: outputPrice,
            PriceCacheReadPerMTok: cacheReadPrice,
            PriceCacheWritePerMTok: cacheWritePrice);
}
