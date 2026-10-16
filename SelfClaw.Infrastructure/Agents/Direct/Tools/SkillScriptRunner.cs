using System.Diagnostics;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// Executes a script a Skill owns. A Skill package is third-party content, so the script runs with the
/// Skill root as its working directory and with a deliberately small environment: inheriting the host's
/// variables would hand every installed Skill whatever the desktop process happens to be carrying.
/// </summary>
internal sealed class SkillScriptRunner
{
    internal const int DefaultTimeoutSeconds = 60;
    private const string TimeoutSubject = "Skill script";

    /// <summary>
    /// Source files are not scripts here: running them needs a project context the Skill cannot supply,
    /// and a half-working compilation path is worse than a clear refusal.
    /// </summary>
    private static readonly HashSet<string> RejectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx"
    };

    private static readonly string[] MinimalEnvironmentVariables =
    [
        "PATH", "Path", "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP",
        "USERPROFILE", "LOCALAPPDATA", "APPDATA", "COMSPEC", "ComSpec",
        "PATHEXT", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS"
    ];

    private readonly Func<string, string?> _resolveExecutable;

    public SkillScriptRunner(Func<string, string?>? resolveExecutable = null)
    {
        _resolveExecutable = resolveExecutable ?? ResolveFromPath;
    }

    /// <summary>
    /// The interpreter each script extension needs, in preference order, with the flags that interpreter
    /// needs before the script path. <c>.sh</c> maps to <c>bash</c> only: falling back to a WSL bridge would
    /// silently pick a different filesystem view of the Skill directory, so a missing <c>bash</c> fails.
    /// </summary>
    internal static IReadOnlyList<InterpreterCandidate> InterpreterCandidates(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".ps1" or ".psm1" =>
                [new InterpreterCandidate("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"])],
            ".cmd" or ".bat" => [new InterpreterCandidate("cmd.exe", ["/c"])],
            ".py" => [new InterpreterCandidate("python", []), new InterpreterCandidate("py", ["-3"])],
            ".js" or ".mjs" or ".cjs" => [new InterpreterCandidate("node", [])],
            ".sh" => [new InterpreterCandidate("bash", [])],
            _ => []
        };

    public async Task<SkillScriptResult> RunAsync(
        string skillRoot,
        string relativePath,
        IReadOnlyList<string>? arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(relativePath);
        if (RejectedExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                $"Skill script '{relativePath}' uses '{extension}', which run_skill_script does not execute.");
        }

        var filePath = SkillPathGuard.ResolveWithinRoot(skillRoot, relativePath);
        if (!File.Exists(filePath))
        {
            return Failed(relativePath, $"Skill script '{relativePath}' was not found.");
        }

        SkillPathGuard.EnsureNoReparsePoints(skillRoot, filePath);
        if (SkillResourceIndexer.Classify(extension) != SkillResourceKind.Script)
        {
            throw new InvalidOperationException($"Skill file '{relativePath}' is not an executable skill script.");
        }

        var result = await ProcessRunner.RunAsync(
                CreateStartInfo(skillRoot, filePath, extension, arguments ?? []),
                timeoutSeconds,
                TimeoutSubject,
                cancellationToken)
            .ConfigureAwait(false);
        return new SkillScriptResult(
            relativePath,
            true,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.OutputTruncated,
            result.ExitCode == 0
                ? $"Skill script '{relativePath}' completed."
                : $"Skill script '{relativePath}' exited with code {result.ExitCode}.");
    }

    private ProcessStartInfo CreateStartInfo(
        string skillRoot,
        string filePath,
        string extension,
        IReadOnlyList<string> arguments)
    {
        var root = Path.GetFullPath(skillRoot);
        var interpreter = ResolveInterpreter(extension);
        var startInfo = new ProcessStartInfo
        {
            FileName = interpreter.Command,
            WorkingDirectory = root
        };
        foreach (var prefixArgument in interpreter.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        startInfo.ArgumentList.Add(filePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in MinimalEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment["SELFCLAW_SKILL_ROOT"] = root;
        return startInfo;
    }

    private InterpreterCandidate ResolveInterpreter(string extension)
    {
        var candidates = InterpreterCandidates(extension);
        foreach (var candidate in candidates)
        {
            var resolved = _resolveExecutable(candidate.Command);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return candidate with { Command = resolved };
            }
        }

        throw new InvalidOperationException(
            $"A '{extension}' Skill script needs '{string.Join(" ' or '", candidates.Select(candidate => candidate.Command))}' on PATH, but none could be found.");
    }

    private static string? ResolveFromPath(string command)
        => ExecutableResolver.ResolvePath(
            command,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetEnvironmentVariable("PATHEXT") ?? ExecutableResolver.DefaultWindowsPathExtensions,
            OperatingSystem.IsWindows());

    private static SkillScriptResult Failed(string relativePath, string message)
        => new(relativePath, false, null, string.Empty, string.Empty, false, message);

    /// <param name="Command">The interpreter name to resolve on PATH, replaced by the resolved path.</param>
    /// <param name="PrefixArguments">Flags the interpreter needs before the script path.</param>
    internal sealed record InterpreterCandidate(string Command, IReadOnlyList<string> PrefixArguments);
}
