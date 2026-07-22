using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed class DispatchingAgentChatRuntime : IAgentChatRuntime
{
    private static readonly TimeSpan AdapterCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyDictionary<AgentExecutionMode, IAgentRuntimeAdapter> _adapters;
    private readonly ILogger<DispatchingAgentChatRuntime> _logger;

    internal DispatchingAgentChatRuntime(
        IEnumerable<IAgentRuntimeAdapter> adapters,
        ILogger<DispatchingAgentChatRuntime>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(adapter => adapter.Mode);
        _logger = logger ?? NullLogger<DispatchingAgentChatRuntime>.Instance;
    }

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _adapters.TryGetValue(request.Agent.Mode, out var adapter)
            ? EnforceProtocolAsync(adapter, request, cancellationToken)
            : UnsupportedMode(request.Agent.Mode, cancellationToken);
    }

    private async IAsyncEnumerable<AgentStreamEvent> EnforceProtocolAsync(
        IAgentRuntimeAdapter adapter,
        ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var executionCancellation = new CancellationTokenSource();
        var cancellationRegistration = cancellationToken.Register(executionCancellation.Cancel);
        var cancellationForwarding = true;
        var adapterEnded = false;
        RunCompletedEvent? terminal = null;
        CancellationTokenSource? cleanupCancellation = null;
        var enumerator = adapter
            .StreamTurnAsync(request, executionCancellation.Token)
            .GetAsyncEnumerator();

        try
        {
            while (true)
            {
                AgentStreamEvent? streamEvent = null;
                Exception? failure = null;
                var hasNext = false;

                try
                {
                    cleanupCancellation?.Token.ThrowIfCancellationRequested();
                    hasNext = terminal is null
                        ? await enumerator.MoveNextAsync().ConfigureAwait(false)
                        : await enumerator.MoveNextAsync().AsTask()
                            .WaitAsync(cleanupCancellation!.Token)
                            .ConfigureAwait(false);
                    if (hasNext)
                    {
                        streamEvent = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (
                    terminal is not null && cleanupCancellation?.IsCancellationRequested == true)
                {
                    _logger.LogWarning(
                        "The {Mode} runtime adapter did not finish within the cleanup timeout after its terminal event.",
                        adapter.Mode);
                    executionCancellation.Cancel();
                    break;
                }
                catch (OperationCanceledException exception) when (terminal is not null)
                {
                    _logger.LogWarning(
                        exception,
                        "The {Mode} runtime adapter canceled after its terminal event.",
                        adapter.Mode);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (failure is not null)
                {
                    if (terminal is null)
                    {
                        _logger.LogError(failure, "The {Mode} runtime adapter failed before a terminal event.", adapter.Mode);
                        terminal = Failed(failure.Message);
                    }
                    else
                    {
                        _logger.LogWarning(failure, "The {Mode} runtime adapter failed after its terminal event.", adapter.Mode);
                    }

                    break;
                }

                if (!hasNext)
                {
                    adapterEnded = true;
                    break;
                }

                if (terminal is not null)
                {
                    _logger.LogWarning(
                        "The {Mode} runtime adapter emitted {EventType} after its terminal event; the event was discarded.",
                        adapter.Mode,
                        streamEvent!.GetType().Name);
                    continue;
                }

                if (streamEvent is RunCompletedEvent completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cancellationRegistration.Dispose();
                    if (executionCancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    terminal = completed;
                    cancellationForwarding = false;
                    cleanupCancellation = new CancellationTokenSource(AdapterCleanupTimeout);
                    continue;
                }

                yield return streamEvent!;
            }

            terminal ??= Failed($"The {adapter.Mode} runtime ended without a completion status.");
        }
        finally
        {
            if (cancellationForwarding)
            {
                cancellationRegistration.Dispose();
            }

            cleanupCancellation?.Dispose();

            if (!adapterEnded)
            {
                executionCancellation.Cancel();
            }

            await DisposeAdapterAsync(enumerator, adapter.Mode).ConfigureAwait(false);
        }

        yield return terminal;
    }

    private async Task DisposeAdapterAsync(
        IAsyncEnumerator<AgentStreamEvent> enumerator,
        AgentExecutionMode mode)
    {
        try
        {
            await enumerator.DisposeAsync().AsTask()
                .WaitAsync(AdapterCleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out while disposing the {Mode} runtime adapter.", mode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to dispose the {Mode} runtime adapter.", mode);
        }
    }

    private static async IAsyncEnumerable<AgentStreamEvent> UnsupportedMode(
        AgentExecutionMode mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return Failed($"Execution mode '{mode}' is not supported.");
    }

    private static RunCompletedEvent Failed(string message)
        => new(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);
}
