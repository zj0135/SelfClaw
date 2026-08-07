using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Runtime;

public sealed class SubagentCompletionEnvelopeFactoryTests
{
    [Fact]
    public void Create_serializes_a_pending_terminal_envelope_with_exact_byte_count()
    {
        var factory = new SubagentCompletionEnvelopeFactory();
        var task = CreateTask("review", "done", SubagentTaskStatus.Succeeded);

        var delivery = factory.Create(task);

        delivery.Status.Should().Be(SubagentDeliveryStatus.Pending);
        delivery.TaskId.Should().Be(task.Id);
        delivery.EnvelopeBytes.Should().Be(Encoding.UTF8.GetByteCount(delivery.EnvelopeJson));
        using var document = JsonDocument.Parse(delivery.EnvelopeJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("Succeeded");
        document.RootElement.GetProperty("result").GetProperty("finalText").GetString().Should().Be("done");
        document.RootElement.GetProperty("result").GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Create_truncates_on_rune_boundaries_and_never_exceeds_32_kib()
    {
        var factory = new SubagentCompletionEnvelopeFactory();
        var task = CreateTask("review", string.Concat(Enumerable.Repeat("A😀", 20000)), SubagentTaskStatus.Succeeded);

        var delivery = factory.Create(task);

        delivery.EnvelopeBytes.Should().BeLessThanOrEqualTo(SubagentCompletionEnvelopeFactory.MaximumEnvelopeBytes);
        using var document = JsonDocument.Parse(delivery.EnvelopeJson);
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var finalText = result.GetProperty("finalText").GetString();
        finalText.Should().NotContain("�");
        (task.FinalText ?? string.Empty).Length.Should().BeGreaterThan((finalText ?? string.Empty).Length);
    }

    [Fact]
    public void Create_preserves_metadata_while_truncating_task_and_error_after_final_text()
    {
        var factory = new SubagentCompletionEnvelopeFactory();
        var task = CreateTask(
            string.Concat(Enumerable.Repeat("task😀", 10000)),
            string.Concat(Enumerable.Repeat("result😀", 10000)),
            SubagentTaskStatus.Failed) with
        {
            ErrorCode = "ProviderFailed",
            ErrorMessage = string.Concat(Enumerable.Repeat("error😀", 10000))
        };

        var delivery = factory.Create(task);

        delivery.EnvelopeBytes.Should().BeLessThanOrEqualTo(SubagentCompletionEnvelopeFactory.MaximumEnvelopeBytes);
        using var document = JsonDocument.Parse(delivery.EnvelopeJson);
        var root = document.RootElement;
        root.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        root.GetProperty("result").GetProperty("errorCode").GetString().Should().Be("ProviderFailed");
        root.GetProperty("result").GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    private static SubagentTaskRecord CreateTask(
        string taskText,
        string? finalText,
        SubagentTaskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentTaskRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "reviewer",
            "Reviewer",
            taskText,
            status,
            1,
            null,
            "{}",
            "{}",
            Guid.NewGuid(),
            900,
            finalText,
            12,
            8,
            status == SubagentTaskStatus.Succeeded ? null : "ProviderFailed",
            status == SubagentTaskStatus.Succeeded ? null : "failed",
            null,
            now.AddSeconds(-2),
            now.AddSeconds(-1),
            now,
            now.AddSeconds(-2),
            now);
    }
}
