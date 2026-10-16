using System.Text.RegularExpressions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// Resolves a bare command name (e.g. <c>claude</c>) into a launch-ready
/// <see cref="CommandInvocation"/> by walking <c>PATH</c> and trying each <c>PATHEXT</c>
/// extension, mirroring how the OS shell locates an executable.
/// <para>
/// On Windows the user-facing <c>claude</c> entry point is usually <c>claude.cmd</c>, a batch
/// shim that cannot be launched directly via <c>CreateProcess</c> — it must run under
/// <c>cmd.exe /c</c>. When the resolved target is a <c>.cmd</c>/<c>.bat</c> file this class wraps
/// it with <c>cmd.exe</c> and produces a pre-escaped verbatim command line (the
/// <c>createCommandInvocation</c> behaviour described in plan.md §5). The escaping is a faithful
/// port of the cross-spawn algorithm (https://qntm.org/cmd).
/// </para>
/// </summary>
public sealed partial class CliCommandResolver
{
    private readonly string _path;
    private readonly string _pathExt;
    private readonly string _comSpec;
    private readonly bool _isWindows;
    private readonly Func<string, bool> _fileExists;

    /// <summary>
    /// Creates a resolver bound to the current process environment. The optional parameters exist
    /// for unit tests so PATH resolution and OS behaviour can be driven deterministically.
    /// </summary>
    public CliCommandResolver(
        string? path = null,
        string? pathExt = null,
        string? comSpec = null,
        bool? isWindows = null,
        Func<string, bool>? fileExists = null)
    {
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _path = path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        _pathExt = pathExt
            ?? Environment.GetEnvironmentVariable("PATHEXT")
            ?? (_isWindows ? ExecutableResolver.DefaultWindowsPathExtensions : string.Empty);
        _comSpec = comSpec
            ?? Environment.GetEnvironmentVariable("ComSpec")
            ?? "cmd.exe";
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>
    /// Resolves <paramref name="command"/> against PATH/PATHEXT and produces a launch-ready invocation.
    /// </summary>
    /// <param name="command">A bare command name or an explicit path to an executable.</param>
    /// <param name="arguments">Arguments to pass to the resolved executable.</param>
    /// <returns>The resolved invocation, shell-wrapped when the target is a Windows batch shim.</returns>
    /// <exception cref="FileNotFoundException">The command could not be located on PATH.</exception>
    public CommandInvocation Resolve(string command, IReadOnlyList<string>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command must be a non-empty value.", nameof(command));

        var args = arguments ?? Array.Empty<string>();
        var resolved = ExecutableResolver.ResolvePath(command, _path, _pathExt, _isWindows, _fileExists)
            ?? throw new FileNotFoundException(
                $"Could not locate executable '{command}' on PATH.", command);

        if (_isWindows && IsBatchScript(resolved))
            return WrapWithCmd(resolved, args);

        return new CommandInvocation
        {
            FileName = resolved,
            ArgumentList = args.ToArray(),
        };
    }

    private static bool IsBatchScript(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wraps a batch shim in <c>cmd.exe /d /s /c "..."</c>, producing a verbatim, pre-escaped
    /// command line. A <c>.cmd</c>/<c>.bat</c> target is double-escaped because cmd.exe processes
    /// the metacharacters twice.
    /// </summary>
    private CommandInvocation WrapWithCmd(string resolvedCommand, IReadOnlyList<string> args)
    {
        var escapedCommand = EscapeCommand(resolvedCommand);
        var escapedArgs = args.Select(a => EscapeArgument(a, doubleEscapeMetaChars: true));
        var shellCommand = string.Join(' ', new[] { escapedCommand }.Concat(escapedArgs));

        return new CommandInvocation
        {
            FileName = _comSpec,
            VerbatimArguments = $"/d /s /c \"{shellCommand}\"",
        };
    }

    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])")]
    private static partial Regex MetaCharsRegex();

    [GeneratedRegex(@"(\\*)""")]
    private static partial Regex BackslashesBeforeQuoteRegex();

    [GeneratedRegex(@"(\\*)$")]
    private static partial Regex TrailingBackslashesRegex();

    private static string EscapeCommand(string arg) =>
        MetaCharsRegex().Replace(arg, "^$1");

    private static string EscapeArgument(string arg, bool doubleEscapeMetaChars)
    {
        arg ??= string.Empty;

        // Double up any backslashes that precede a double quote, then escape that quote.
        arg = BackslashesBeforeQuoteRegex().Replace(arg, "$1$1\\\"");
        // Double up a trailing run of backslashes (the closing quote turns them significant).
        arg = TrailingBackslashesRegex().Replace(arg, "$1$1");
        // All remaining backslashes are literal; wrap the whole token in quotes.
        arg = "\"" + arg + "\"";
        // Escape cmd metacharacters with a caret.
        arg = MetaCharsRegex().Replace(arg, "^$1");
        if (doubleEscapeMetaChars)
            arg = MetaCharsRegex().Replace(arg, "^$1");

        return arg;
    }
}
