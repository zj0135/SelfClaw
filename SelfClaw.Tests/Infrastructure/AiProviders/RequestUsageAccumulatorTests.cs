using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class RequestUsageAccumulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incremental_total_only_and_derived_total_preserve_both_fragments(bool reverse)
    {
        UsageDetails[] fragments = [new() { TotalTokenCount = 10 }, new() { InputTokenCount = 3, OutputTokenCount = 4 }];
        var accumulator = new RequestUsageAccumulator(AiUsageUpdateKind.Incremental);
        foreach (var fragment in reverse ? fragments.Reverse() : fragments) accumulator.Observe(fragment);
        var usage = accumulator.Build();
        usage.Should().NotBeNull();
        usage!.TotalTokenCount.Should().Be(17);
        usage.InputTokenCount.Should().Be(3);
        usage.OutputTokenCount.Should().Be(4);
        accumulator.Build()!.TotalTokenCount.Should().Be(17, "reading a snapshot must not mutate accumulation");
        fragments[1].TotalTokenCount.Should().BeNull("normalization must not modify adapter objects");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Incremental_all_reported_or_all_derived_totals_are_added_once(bool reported)
    {
        var accumulator = new RequestUsageAccumulator(AiUsageUpdateKind.Incremental);
        accumulator.Observe(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4, TotalTokenCount = reported ? 8 : null });
        accumulator.Observe(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 6, TotalTokenCount = reported ? 12 : null });
        accumulator.Build()!.TotalTokenCount.Should().Be(reported ? 20 : 18, "reported totals take precedence over derivation");
    }

    [Fact]
    public void Incremental_missing_components_do_not_invent_a_complete_total_from_other_fragments()
    {
        var accumulator = new RequestUsageAccumulator(AiUsageUpdateKind.Incremental);
        accumulator.Observe(new UsageDetails { TotalTokenCount = 10 });
        accumulator.Observe(new UsageDetails { InputTokenCount = 3 });
        accumulator.Build()!.TotalTokenCount.Should().BeNull();
        accumulator.Observe(new UsageDetails { OutputTokenCount = 4 });
        var usage = accumulator.Build()!;
        usage.TotalTokenCount.Should().BeNull("separate incremental fragments cannot supply one another's missing counters");
        usage.InputTokenCount.Should().Be(3);
        usage.OutputTokenCount.Should().Be(4);
    }
}
