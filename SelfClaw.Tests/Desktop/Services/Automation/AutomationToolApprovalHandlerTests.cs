using FluentAssertions;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Automation;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationToolApprovalHandlerTests
{
    [Fact]
    public async Task RequestApprovalAsync_denies_immediately_and_records_a_visible_diagnostic()
    {
        var logger = new CapturingLogger();
        var handler = new AutomationToolApprovalHandler(logger);
        var request = new ToolApprovalRequest(
            Guid.NewGuid(),
            "write_file",
            "Write file",
            "Writes a file.",
            "{\"path\":\"a.txt\"}",
            ConversationId: Guid.NewGuid());

        var completion = handler.RequestApprovalAsync(request);

        completion.IsCompleted.Should().BeTrue();
        (await completion).Should().Be(ToolApprovalDecision.Denied);
        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("write_file").And.Contain("Automation hook-forced approval denied");
    }

    [Fact]
    public void TryResolve_never_resurrects_a_denied_request()
        => new AutomationToolApprovalHandler(new CapturingLogger())
            .TryResolve(Guid.NewGuid(), ToolApprovalDecision.Approved)
            .Should().BeFalse();

    private sealed class CapturingLogger : ILogger<AutomationToolApprovalHandler>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
