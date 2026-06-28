using System.Text.RegularExpressions;

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
            ?? (_isWindows ? ".COM;.EXE;.BAT;.CMD" : string.Empty);
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
        var resolved = ResolveExecutablePath(command)
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

    /// <summary>
    /// Locates the absolute path of <paramref name="command"/>, returning <c>null</c> if not found.
    /// An explicit path (containing a directory separator) is resolved directly; a bare name is
    /// searched across every PATH directory and PATHEXT extension.
    /// </summary>
    private string? ResolveExecutablePath(string command)
    {
        var hasDirectory = command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0;
        if (hasDirectory)
        {
            var full = Path.GetFullPath(command);
            return ProbeWithExtensions(full);
        }

        foreach (var dir in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, command);
            }
            catch (ArgumentException)
            {
                // Skip PATH entries containing invalid path characters.
                continue;
            }

            var hit = ProbeWithExtensions(candidate);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    /// <summary>
    /// Probes a candidate path as-is and with each PATHEXT extension appended, returning the first
    /// existing file. The as-is probe handles commands given with an explicit extension.
    /// </summary>
    private string? ProbeWithExtensions(string candidate)
    {
        if (_fileExists(candidate))
            return candidate;

        if (!_isWindows)
            return null;

        // Only append PATHEXT when the candidate lacks a recognised executable extension.
        var existingExt = Path.GetExtension(candidate);
        if (existingExt.Length > 0 && PathExtContains(existingExt))
            return null;

        foreach (var ext in _pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = ext.Trim();
            if (normalized.Length == 0)
                continue;
            if (!normalized.StartsWith('.'))
                normalized = "." + normalized;

            var withExt = candidate + normalized;
            if (_fileExists(withExt))
                return withExt;
        }

        return null;
    }

    private bool PathExtContains(string extension) =>
        _pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(e => string.Equals(e.Trim(), extension, StringComparison.OrdinalIgnoreCase));

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
