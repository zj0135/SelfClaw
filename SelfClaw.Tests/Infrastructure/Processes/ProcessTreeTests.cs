using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Tests.Infrastructure.Processes;

public sealed class ProcessTreeTests
{
    /// <summary>
    /// Every shape a failed termination is allowed to surface as. Whenever one of these escapes
    /// <see cref="ProcessTree.TryKill"/> it does so from a cancellation callback or a finally block, where it
    /// replaces the caller's <see cref="OperationCanceledException"/> (the turn is recorded as failed and its
    /// queue is paused) or skips the rest of the cleanup.
    /// </summary>
    public static TheoryData<Exception> ExpectedTerminationFailures => new()
    {
        new InvalidOperationException("the process has already exited"),
        new Win32Exception(5, "access is denied"),
        new NotSupportedException("remote process"),
        // Kill(entireProcessTree: true) reports "not all processes in the descendant tree could be
        // terminated" this way: a partial result, and the one shape that used to escape.
        new AggregateException(new Win32Exception(5, "access is denied"))
    };

    [Theory]
    [MemberData(nameof(ExpectedTerminationFailures))]
    public void A_failed_termination_is_swallowed(Exception failure)
        => ProcessTree.IsTerminationFailure(failure).Should().BeTrue();

    [Fact]
    public void An_unexpected_exception_is_not_swallowed()
    {
        // The set stays a whitelist: a genuine bug inside the kill must not be hidden behind a catch-all.
        // ObjectDisposedException is deliberately not in it as its own entry - it derives from
        // InvalidOperationException and is already covered.
        ProcessTree.IsTerminationFailure(new InvalidCastException()).Should().BeFalse();
        ProcessTree.IsTerminationFailure(new System.IO.IOException()).Should().BeFalse();
    }

    [Fact]
    public void TryKill_is_silent_for_a_process_that_already_exited()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/s", "/c", "exit 0"])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit(30_000).Should().BeTrue();

        // HasExited is true, so nothing is killed; the guard has to tolerate the state instead of throwing
        // into whatever cleanup path called it.
        var killed = () => ProcessTree.TryKill(process);
        killed.Should().NotThrow();
    }
}
