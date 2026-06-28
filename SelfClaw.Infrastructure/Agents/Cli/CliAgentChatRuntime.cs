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

        var kind = ResolveAgentKind(request.Agent);
        var definition = _registry.Find(kind);
        if (definition is null)
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: $"No CLI agent definition is registered for '{kind}'.");
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
            AgentKind = kind,
            WorkingDirectory = ResolveWorkingDirectory(request.WorkspaceRoot),
            ResumeSessionId = sessionPlan.ResumeSessionId,
            NewSessionId = sessionPlan.NewSessionId,
            SystemPrompt = ComposeSystemPrompt(request.Agent),
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
            Environment = BuildEnvironment(kind, request),
        };

        var parser = CreateParser(definition.StreamFormat);

        await using var session = _processHost.Start(startInfo, cancellationToken);

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
            _logger.LogWarning(ex, "Failed to write the prompt to the {Kind} CLI agent.", kind);
            writeError = ex.Message;
        }

        var runCompletedEmitted = false;

        if (writeError is null)
        {
            await foreach (var line in session.ReadOutputLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var streamEvent in parser.Feed(line))
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

    /// <summary>
    /// Resolves which CLI the agent targets. The first version registers Claude Code only, so every
    /// CLI agent maps to <see cref="CliAgentKind.Claude"/>; Codex / OpenCode selection lands in 阶段 7.
    /// </summary>
    private static CliAgentKind ResolveAgentKind(AgentRuntimeDefinition agent) => CliAgentKind.Claude;

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
        return string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root;
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

    /// <summary>
    /// Builds the environment overrides passed to the child so provider key / base-url / model travel
    /// out-of-band rather than on the command line (plan.md §5, §7). Claude Code reads the
    /// <c>ANTHROPIC_*</c> variables; Codex / OpenCode mappings arrive with 阶段 7.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> BuildEnvironment(
        CliAgentKind kind,
        ChatTurnRequest request)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        switch (kind)
        {
            case CliAgentKind.Claude:
                if (!string.IsNullOrWhiteSpace(request.ApiKey))
                    environment["ANTHROPIC_API_KEY"] = request.ApiKey;
                if (!string.IsNullOrWhiteSpace(request.Profile.Endpoint))
                    environment["ANTHROPIC_BASE_URL"] = request.Profile.Endpoint;
                if (!string.IsNullOrWhiteSpace(request.Profile.Model))
                    environment["ANTHROPIC_MODEL"] = request.Profile.Model;
                break;
        }

        return environment;
    }

    private static IAgentStreamParser CreateParser(CliStreamFormat format) => format switch
    {
        CliStreamFormat.ClaudeStreamJson => new ClaudeStreamJsonParser(),
        // JsonEventStreamParser (Codex / OpenCode) lands in 阶段 7.
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
