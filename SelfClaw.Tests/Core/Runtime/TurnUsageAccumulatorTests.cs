using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Tests.Core.Runtime;

public sealed class TurnUsageAccumulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normalized_unknown_total_survives_accumulation_and_another_consumer(bool reverse)
    {
        TurnUsage[] contributions =
        [
            new(InputTokens: 10, OutputTokens: 2, TotalTokens: 12),
            new(InputTokens: 7)
        ];
        var accumulator = new TurnUsageAccumulator();
        foreach (var usage in reverse ? contributions.Reverse() : contributions) accumulator.Observe(usage);
        var normalized = accumulator.Build()!;
        normalized.TotalTokens.Should().BeNull("one observed contribution has an explicitly unknown total");
        normalized.InputTokens.Should().Be(17);
        normalized.OutputTokens.Should().Be(2);
        var recorder = new TurnUsageAccumulator();
        recorder.Observe(normalized);
        recorder.Build().Should().BeEquivalentTo(normalized);
    }

    [Fact]
    public void Complete_components_do_not_authorize_deriving_an_explicitly_unknown_total()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: null));
        accumulator.Build()!.TotalTokens.Should().BeNull();
    }

    [Fact]
    public void Build_returns_null_without_observations()
    {
        var accumulator = new TurnUsageAccumulator();

        accumulator.HasObservations.Should().BeFalse();
        accumulator.Build().Should().BeNull();
    }

    [Fact]
    public void Observe_sums_tokens_across_provider_calls_and_keeps_the_latest_context()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(
            Model: "first-model",
            InputTokens: 100,
            CachedInputTokens: 40,
            CacheWriteInputTokens: 10,
            OutputTokens: 20,
            TotalTokens: 120,
            ProviderCalls: 1,
            ContextTokens: 130));
        accumulator.Observe(new TurnUsage(
            Model: "last-model",
            InputTokens: 150,
            CachedInputTokens: 50,
            OutputTokens: 30,
            TotalTokens: 180,
            ProviderCalls: 2,
            ContextTokens: 190,
            ContextWindowTokens: 200_000));

        var usage = accumulator.Build();

        usage.Should().NotBeNull();
        usage!.Model.Should().Be("last-model");
        usage.InputTokens.Should().Be(250);
        usage.CachedInputTokens.Should().Be(90);
        usage.CacheWriteInputTokens.Should().Be(10);
        usage.UncachedInputTokens.Should().Be(150);
        usage.OutputTokens.Should().Be(50);
        usage.TotalTokens.Should().Be(300);
        usage.ProviderCalls.Should().Be(3);
        usage.ContextTokens.Should().Be(190);
        usage.ContextWindowTokens.Should().Be(200_000);
    }

    [Fact]
    public void Observe_prefers_a_provider_reported_cost_over_summed_estimates()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 100, CostSource: TurnUsageCostSource.Estimated));
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 200, CostSource: TurnUsageCostSource.Estimated));
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 900, CostSource: TurnUsageCostSource.ProviderReported));

        var usage = accumulator.Build();

        usage!.CostUsdMicros.Should().Be(900);
        usage.CostSource.Should().Be(TurnUsageCostSource.ProviderReported);
    }

    [Fact]
    public void Observe_sums_provider_reported_costs()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 400, CostSource: TurnUsageCostSource.ProviderReported));
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 600, CostSource: TurnUsageCostSource.ProviderReported));

        var usage = accumulator.Build();

        usage!.CostUsdMicros.Should().Be(1_000);
        usage.CostSource.Should().Be(TurnUsageCostSource.ProviderReported);
    }

    [Fact]
    public void Observe_sums_estimated_costs_when_no_provider_cost_is_reported()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 100, CostSource: TurnUsageCostSource.Estimated));
        accumulator.Observe(new TurnUsage(OutputTokens: 1, CostUsdMicros: 250, CostSource: TurnUsageCostSource.Estimated));

        var usage = accumulator.Build();

        usage!.CostUsdMicros.Should().Be(350);
        usage.CostSource.Should().Be(TurnUsageCostSource.Estimated);
    }

    [Fact]
    public void Build_treats_missing_input_counts_as_unknown_rather_than_zero()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(OutputTokens: 7));

        var usage = accumulator.Build();

        usage!.InputTokens.Should().BeNull();
        usage.UncachedInputTokens.Should().BeNull();
        usage.TotalTokens.Should().BeNull();
        usage.CostUsdMicros.Should().BeNull();
        usage.CostSource.Should().Be(TurnUsageCostSource.None);
    }

    [Fact]
    public void Observe_keeps_the_latest_additional_counts()
    {
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(new TurnUsage(OutputTokens: 1, AdditionalCountsJson: """{"a":1}"""));
        accumulator.Observe(new TurnUsage(OutputTokens: 1));
        accumulator.Observe(new TurnUsage(OutputTokens: 1, AdditionalCountsJson: """{"b":2}"""));

        accumulator.Build()!.AdditionalCountsJson.Should().Be("""{"b":2}""");
    }
}
