using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Session;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;
using SelfClaw.Infrastructure.Agents.Cli.Parsers.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Infrastructure.Agents.Cli;

/// <summary>
/// The CLI-backed <see cref="IAgentChatRuntime"/> (plan.md 阶段 5, T5.1). Assembles the phase 2–4
/// building blocks for a single turn and streams the agent's output as <see cref="AgentStreamEvent"/>s:
/// <list type="number">
///   <item>resolve the <see cref="CliAgentDefinition"/> for the requested agent;</item>
///   <item>plan session resume / new-session ids via <see cref="CliSessionResolver"/> (plan.md §6);</item>
///   <item>build the argument vector and resolve the executable via <see cref="CliCommandResolver"/>;</item>
///   <item>launch the subprocess through <see cref="ICliAgentProcessHost"/>, write the JSONL prompt and
///         close stdin to signal EOF (plan.md §5);</item>
///   <item>feed stdout lines into the matching <see cref="IAgentStreamParser"/> and yield its events,
///         capturing the stream-reported session id for <see cref="ResumeStrategy.CapturedFromStream"/>
///         agents;</item>
///   <item>synthesize a terminal <see cref="RunCompletedEvent"/> from the process exit when the stream
///         ended without one.</item>
/// </list>
/// </summary>
public sealed class CliAgentChatRuntime : IAgentChatRuntime
{
    private readonly ICliAgentProcessHost _processHost;
    private readonly CliAgentRegistry _registry;
    private readonly CliCommandResolver _commandResolver;
    private readonly CliSessionResolver _sessionResolver;
    private readonly ILogger<CliAgentChatRuntime> _logger;

    public CliAgentChatRuntime(
        ICliAgentProcessHost processHost,
        CliAgentRegistry registry,
        CliCommandResolver commandResolver,
        CliSessionResolver sessionResolver,
        ILogger<CliAgentChatRuntime>? logger = null)
    {
        _processHost = processHost;
        _registry = registry;
        _commandResolver = commandResolver;
        _sessionResolver = sessionResolver;
        _logger = logger ?? NullLogger<CliAgentChatRuntime>.Instance;
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CliAgent is not { } agentKind)
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: "No local CLI agent is selected. Install Claude Code, Codex CLI or OpenCode and select one in settings.");
            yield break;
        }

        var definition = _registry.Find(agentKind);
        if (definition is null)
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: $"No CLI agent definition is registered for '{agentKind}'.");
            yield break;
        }

        var prompt = ExtractPrompt(request.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: "There is no user prompt to send to the agent.");
            yield break;
        }

        var sessionPlan = await _sessionResolver
            .PrepareAsync(request.ConversationId, definition, cancellationToken)
            .ConfigureAwait(false);

        var runContext = new CliRunContext
        {
            AgentKind = agentKind,
            WorkingDirectory = ResolveWorkingDirectory(request.WorkspaceRoot),
            ResumeSessionId = sessionPlan.ResumeSessionId,
            NewSessionId = sessionPlan.NewSessionId,
            SystemPrompt = ComposeSystemPrompt(request.Agent),
            Model = string.IsNullOrWhiteSpace(request.CliModel) ? null : request.CliModel,
            ReasoningEffort = string.IsNullOrWhiteSpace(request.CliReasoningEffort) ? null : request.CliReasoningEffort,
        };

        // Resolve the executable up front so a missing CLI surfaces as a clean failure rather than an
        // exception bubbling out of the iterator.
        CommandInvocation? invocation = null;
        string? setupError = null;
        try
        {
            var args = definition.BuildArgs(runContext);
            invocation = _commandResolver.Resolve(definition.Command, args);
        }
        catch (FileNotFoundException ex)
        {
            setupError = ex.Message;
        }

        if (invocation is null)
        {
            yield return new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: setupError);
            yield break;
        }

        var startInfo = new CliProcessStartInfo
        {
            Invocation = invocation,
            WorkingDirectory = runContext.WorkingDirectory,
            // The CLI uses its own local configuration (API key / base URL / model), so SelfClaw injects
            // nothing into the child environment. A selected profile, if any, no longer travels to the agent.
        };

        _logger.LogInformation(
            "Starting {Kind} CLI agent. FileName={FileName}, ShellWrapped={ShellWrapped}, Args={Args}, VerbatimArgs={VerbatimArgs}, WorkingDirectory={WorkingDirectory}",
            agentKind,
            invocation.FileName,
            invocation.IsShellWrapped,
            string.Join(' ', invocation.ArgumentList),
            invocation.VerbatimArguments,
            runContext.WorkingDirectory);

        var parser = CreateParser(definition.StreamFormat, agentKind);

        // Launching the child can throw synchronously (e.g. a Win32Exception when the resolved target is
        // not a real executable). Surface it as a clean completion rather than letting it escape the
        // iterator, where it would be swallowed before any assistant message exists.
        ICliAgentProcessSession? session = null;
        string? startError = null;
        try
        {
            session = _processHost.Start(startInfo, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the {Kind} CLI agent process.", agentKind);
            startError = ex.Message;
        }

        if (session is null)
        {
            yield return new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: startError);
            yield break;
        }

        await using var _ = session.ConfigureAwait(false);

        // Deliver the prompt, then close stdin to end the turn. A write failure (e.g. the child died on
        // startup) is captured so we can report it through the normal completion path.
        string? writeError = null;
        try
        {
            foreach (var line in definition.BuildStdinLines(prompt))
                await session.WriteStdinLineAsync(line, cancellationToken).ConfigureAwait(false);
            await session.CompleteStdinAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write the prompt to the {Kind} CLI agent.", agentKind);
            writeError = ex.Message;
        }

        var runCompletedEmitted = false;

        if (writeError is null)
        {
            await foreach (var line in session.ReadOutputLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("{Kind} stdout: {Line}", agentKind, line);
                // ReadOutputLinesAsync yields newline-stripped lines, but the parser splits its input on
                // '\n' internally (it is built to accept raw stdout chunks). Re-append the terminator so
                // each line is parsed immediately instead of being buffered until Flush() concatenates them.
                foreach (var streamEvent in parser.Feed(line + '\n'))
                {
                    if (streamEvent is RunStartedEvent started)
                    {
                        await _sessionResolver
                            .CaptureAsync(request.ConversationId, definition, started.SessionId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (streamEvent is RunCompletedEvent)
                        runCompletedEmitted = true;

                    yield return streamEvent;
                }
            }

            foreach (var streamEvent in parser.Flush())
            {
                if (streamEvent is RunCompletedEvent)
                    runCompletedEmitted = true;

                yield return streamEvent;
            }
        }

        var result = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "{Kind} CLI agent exited. Status={Status}, ExitCode={ExitCode}, TimedOut={TimedOut}, RunCompletedEmitted={Emitted}, StdErr={StdErr}",
            agentKind,
            result.Status,
            result.ExitCode,
            result.TimedOut,
            runCompletedEmitted,
            result.StandardError);

        // The parser emits RunCompletedEvent off the agent's own `result` line. When the stream ends
        // without one (crash, non-zero exit, write failure), synthesize the terminal event from the
        // classified process outcome so the UI always sees a completion (plan.md §5).
        if (!runCompletedEmitted)
        {
            yield return new RunCompletedEvent(
                result.Status,
                FinalText: null,
                ErrorMessage: writeError ?? BuildExitError(result));
        }
    }

    /// <summary>The latest user message is the prompt; the CLI keeps prior turns via session resume.</summary>
    private static string? ExtractPrompt(IReadOnlyList<MessageRecord> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.User)
                return messages[i].MarkdownContent;
        }

        return null;
    }

    private static string ResolveWorkingDirectory(WorkspaceRoot? workspaceRoot)
    {
        var root = workspaceRoot?.RootPath;
        if (!string.IsNullOrWhiteSpace(root))
            return root;

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath) && Directory.Exists(desktopPath))
            return desktopPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? Path.GetTempPath() : userProfile;
    }

    /// <summary>
    /// Assembles the system prompt injected into the agent (plan.md §8, T5.3). For now this is the
    /// agent's own instructions; it is the seam where design-system / persona context is layered in.
    /// </summary>
    private static string? ComposeSystemPrompt(AgentRuntimeDefinition agent)
    {
        var instructions = agent.Instructions?.Trim();
        return string.IsNullOrEmpty(instructions) ? null : instructions;
    }

    private static IAgentStreamParser CreateParser(CliStreamFormat format, CliAgentKind kind) => format switch
    {
        CliStreamFormat.ClaudeStreamJson => new ClaudeStreamJsonParser(),
        CliStreamFormat.JsonEventStream => new JsonEventStreamParser(kind),
        _ => throw new NotSupportedException($"No stream parser is available for '{format}'."),
    };

    private static string? BuildExitError(CliProcessResult result)
    {
        if (result.Status == RunCompletionStatus.Succeeded)
            return null;
        if (result.TimedOut)
            return "The agent process was stopped after exceeding the inactivity timeout.";
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            return result.StandardError;

        return result.ExitCode is { } code
            ? $"The agent process exited with code {code}."
            : "The agent process ended unexpectedly.";
    }
}
