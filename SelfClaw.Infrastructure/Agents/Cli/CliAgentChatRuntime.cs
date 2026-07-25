using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;
using SelfClaw.Infrastructure.Agents.Cli.Session.Abstractions;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;

namespace SelfClaw.Infrastructure.Agents.Cli;

/// <summary>
/// The CLI adapter used behind the external <see cref="IAgentChatRuntime"/> seam. It assembles the
/// building blocks for a single turn and streams the agent's output as <see cref="AgentStreamEvent"/>s:
/// <list type="number">
///   <item>select the requested CLI adapter and prepare its command, input and parser;</item>
///   <item>load the conversation's stored session id and let the adapter apply its resume rules;</item>
///   <item>resolve the executable via <see cref="CliCommandResolver"/>;</item>
///   <item>launch the subprocess through <see cref="ICliAgentProcessHost"/>, write the prepared input and
///         close stdin to signal EOF;</item>
///   <item>feed stdout lines into the matching <see cref="CliStreamParser"/> and yield its events,
///         capturing the stream-reported session id for the next turn;</item>
///   <item>synthesize a candidate terminal <see cref="RunCompletedEvent"/> from the process exit when the stream
///         ended without one.</item>
/// </list>
/// </summary>
internal sealed class CliAgentChatRuntime : IAgentRuntimeAdapter
{
    private readonly ICliAgentProcessHost _processHost;
    private readonly CliAgentAdapterRegistry _registry;
    private readonly CliCommandResolver _commandResolver;
    private readonly ICliAgentSessionStore _sessionStore;
    private readonly ILogger<CliAgentChatRuntime> _logger;

    public CliAgentChatRuntime(
        ICliAgentProcessHost processHost,
        CliAgentAdapterRegistry registry,
        CliCommandResolver commandResolver,
        ICliAgentSessionStore sessionStore,
        ILogger<CliAgentChatRuntime>? logger = null)
    {
        _processHost = processHost;
        _registry = registry;
        _commandResolver = commandResolver;
        _sessionStore = sessionStore;
        _logger = logger ?? NullLogger<CliAgentChatRuntime>.Instance;
    }

    public AgentExecutionMode Mode => AgentExecutionMode.Cli;

    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The dispatcher routes by Mode, so a CLI turn always arrives as a CliChatTurnRequest.
        var cliRequest = request as CliChatTurnRequest
            ?? throw new ArgumentException(
                $"The CLI runtime requires a {nameof(CliChatTurnRequest)}.", nameof(request));

        if (cliRequest.CliAgent is not { } agentKind)
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: "No local CLI agent is selected. Install Claude Code, Codex CLI or OpenCode and select one in settings.");
            yield break;
        }

        var adapter = _registry.Find(agentKind);
        if (adapter is null)
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: $"No CLI agent adapter is registered for '{agentKind}'.");
            yield break;
        }

        var prompt = ExtractPrompt(cliRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return new RunCompletedEvent(
                RunCompletionStatus.Failed,
                FinalText: null,
                ErrorMessage: "There is no user prompt to send to the agent.");
            yield break;
        }

        var storedSessionId = await _sessionStore
            .GetSessionIdAsync(cliRequest.ConversationId, agentKind, cancellationToken)
            .ConfigureAwait(false);

        var preparation = new CliTurnPreparation(
            prompt,
            storedSessionId,
            ComposeSystemPrompt(cliRequest.Agent),
            string.IsNullOrWhiteSpace(cliRequest.CliModel) ? null : cliRequest.CliModel,
            string.IsNullOrWhiteSpace(cliRequest.CliReasoningEffort) ? null : cliRequest.CliReasoningEffort);

        // Resolve the executable up front so a missing CLI surfaces as a clean failure rather than an
        // exception bubbling out of the iterator.
        PreparedCliTurn? preparedTurn = null;
        CommandInvocation? invocation = null;
        string? setupError = null;
        try
        {
            preparedTurn = adapter.PrepareTurn(preparation);
            invocation = _commandResolver.Resolve(preparedTurn.Command, preparedTurn.Arguments);
        }
        catch (FileNotFoundException ex)
        {
            setupError = ex.Message;
        }

        if (preparedTurn is null || invocation is null)
        {
            yield return new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: setupError);
            yield break;
        }

        var startInfo = new CliProcessStartInfo
        {
            Invocation = invocation,
            WorkingDirectory = ResolveWorkingDirectory(cliRequest.WorkspaceRoot),
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
            startInfo.WorkingDirectory);

        var parser = preparedTurn.Parser;

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
            foreach (var line in preparedTurn.StandardInputLines)
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
                foreach (var streamEvent in parser.ParseLine(line))
                {
                    if (streamEvent is RunStartedEvent started)
                    {
                        if (!string.IsNullOrWhiteSpace(started.SessionId))
                        {
                            await _sessionStore
                                .SetSessionIdAsync(cliRequest.ConversationId, agentKind, started.SessionId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    if (streamEvent is RunCompletedEvent)
                        runCompletedEmitted = true;

                    yield return streamEvent;
                }
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
        // classified process outcome so the dispatcher always receives a completion candidate.
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
    /// Assembles the system prompt injected into the agent. For now this is the agent's own instructions.
    /// </summary>
    private static string? ComposeSystemPrompt(AgentRuntimeDefinition agent)
    {
        var instructions = agent.Instructions?.Trim();
        return string.IsNullOrEmpty(instructions) ? null : instructions;
    }

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
