namespace SelfClaw.Infrastructure.Agents.Cli.Process.Models;

/// <summary>
/// A fully resolved, launch-ready command produced by <see cref="CliCommandResolver"/>.
/// On Windows a <c>.cmd</c> / <c>.bat</c> entry point is wrapped with <c>cmd.exe /c</c>,
/// in which case <see cref="VerbatimArguments"/> carries the pre-escaped command line and
/// <see cref="ArgumentList"/> is empty. Otherwise the executable is launched directly and
/// <see cref="ArgumentList"/> is escaped by the runtime when the process starts.
/// </summary>
public sealed record CommandInvocation
{
    /// <summary>The executable to launch (resolved absolute path, or <c>cmd.exe</c> when shell-wrapped).</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Arguments passed individually to <c>ProcessStartInfo.ArgumentList</c> (the runtime escapes them).
    /// Empty when <see cref="VerbatimArguments"/> is set.
    /// </summary>
    public IReadOnlyList<string> ArgumentList { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A pre-built, already-escaped command line assigned verbatim to <c>ProcessStartInfo.Arguments</c>.
    /// Non-null only when the command is shell-wrapped (e.g. a <c>.cmd</c> launched via <c>cmd.exe</c>).
    /// </summary>
    public string? VerbatimArguments { get; init; }

    /// <summary>True when the command is wrapped by <c>cmd.exe</c> (Windows batch entry point).</summary>
    public bool IsShellWrapped => VerbatimArguments is not null;
}
