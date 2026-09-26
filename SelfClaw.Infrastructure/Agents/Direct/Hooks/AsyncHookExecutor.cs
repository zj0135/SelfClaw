using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// Host-level bounded executor for hooks that do not block the turn (runCompleted,
/// httpResponseReceived and async toolExecuted). Work is keyed by (plugin, hook) so one hook's
/// invocations stay FIFO while different hooks run in parallel, and every item holds its own Plugin
/// version lease until it finishes, is dropped or is evicted.
/// </summary>
internal sealed class AsyncHookExecutor : IHostedService
{
    private const int WorkerCount = 4;
    private const int MaximumQueuedItems = 256;
    private const int MaximumQueuedItemsPerPlugin = 64;
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);

    private readonly Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> _runner;
    private readonly PluginHookExecutionLog _log;
    private readonly IPluginVersionLeaseManager _leaseManager;
    private readonly ILogger<AsyncHookExecutor> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<WorkItem>> _queues = new(StringComparer.Ordinal);
    private readonly Queue<string> _readyKeys = new();
    private readonly HashSet<string> _runningKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkItem> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pluginQueued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pluginEviction = new(StringComparer.Ordinal);
    private readonly List<Task> _workers = [];
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _stop = new();
    private int _queuedCount;
    private bool _started;
    private bool _stopped;

    public AsyncHookExecutor(
        CommandHookRunner runner,
        PluginHookExecutionLog log,
        IPluginVersionLeaseManager leaseManager,
        ILogger<AsyncHookExecutor>? logger = null)
        : this(runner.RunAsync, log, leaseManager, logger)
    {
    }

    /// <summary>
    /// The delegate form exists so queueing, eviction and lease discipline can be tested without
    /// spawning processes; the container always builds the executor over the real runner.
    /// </summary>
    internal AsyncHookExecutor(
        Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> runner,
        PluginHookExecutionLog log,
        IPluginVersionLeaseManager leaseManager,
        ILogger<AsyncHookExecutor>? logger = null)
    {
        _runner = runner;
        _log = log;
        _leaseManager = leaseManager;
        _logger = logger ?? NullLogger<AsyncHookExecutor>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            for (var index = 0; index < WorkerCount; index++)
            {
                _workers.Add(Task.Run(() => WorkerLoopAsync(_stop.Token)));
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<WorkItem> queued;
        List<WorkItem> running;
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            queued = DrainQueues();
            running = [.. _running.Values];
        }

        foreach (var item in queued)
        {
            Complete(item, "dropped", "The executor stopped before this hook ran.", null);
        }

        var inFlight = Task.WhenAll(running.Select(item => item.Completion.Task));
        var cancelled = false;
        try
        {
            await inFlight.WaitAsync(DrainWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        _stop.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        if (cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public bool TryEnqueue(AsyncHookWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var key = Key(work.Hook);
        WorkItem? item = null;
        string? rejection = null;
        lock (_sync)
        {
            if (_stopped || !_started)
            {
                rejection = "The hook executor is not running.";
            }
            else if (_queuedCount >= MaximumQueuedItems ||
                     _pluginQueued.GetValueOrDefault(work.Hook.PluginId) >= MaximumQueuedItemsPerPlugin)
            {
                rejection = "The hook executor queue is full.";
            }
            else
            {
                IDisposable? lease = null;
                try
                {
                    lease = _leaseManager.Acquire(work.Hook.PluginRoot);
                }
                catch (InvalidOperationException exception)
                {
                    rejection = exception.Message;
                }

                if (lease is not null)
                {
                    item = new WorkItem(work, lease, EvictionSource(work.Hook.PluginId));
                    if (!_queues.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<WorkItem>();
                        _queues[key] = queue;
                    }

                    queue.Enqueue(item);
                    _readyKeys.Enqueue(key);
                    _queuedCount++;
                    _pluginQueued[work.Hook.PluginId] = _pluginQueued.GetValueOrDefault(work.Hook.PluginId) + 1;
                }
            }
        }

        if (item is null)
        {
            LogEntry(work, "dropped", TimeSpan.Zero, null, rejection);
            _logger.LogWarning(
                "Dropped async hook '{PluginId}/{HookId}' ({EventName}): {Reason}",
                work.Hook.PluginId, work.Hook.Contribution.Id, work.EventName, rejection);
            return false;
        }

        _ready.Release();
        return true;
    }

    /// <summary>
    /// Removes this Plugin's queued work and cancels its in-flight runs. Later enqueues for the same
    /// Plugin get a fresh token source, so a finished eviction does not affect them.
    /// </summary>
    public async Task EvictPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        List<WorkItem> queued = [];
        List<WorkItem> running;
        CancellationTokenSource? previous;
        lock (_sync)
        {
            foreach (var key in _queues.Keys.Where(key => KeyPlugin(key) == pluginId).ToArray())
            {
                var queue = _queues[key];
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    _queuedCount--;
                    _pluginQueued[pluginId] = _pluginQueued.GetValueOrDefault(pluginId) - 1;
                    queued.Add(item);
                }

                _queues.Remove(key);
            }

            running = [.. _running.Values.Where(item => item.PluginId == pluginId)];
            previous = _pluginEviction.Remove(pluginId, out var existing) ? existing : null;
            _pluginEviction[pluginId] = new CancellationTokenSource();
        }

        foreach (var item in queued)
        {
            Complete(item, "evicted", "The Plugin was disabled or deleted before this hook ran.", null);
        }

        previous?.Cancel();
        try
        {
            if (running.Count > 0)
            {
                var inFlight = Task.WhenAll(running.Select(item => item.Completion.Task));
                await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken stopToken)
    {
        while (true)
        {
            try
            {
                await _ready.WaitAsync(stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var (key, item) = TakeReadyItem();
            if (item is null)
            {
                continue;
            }

            await ExecuteAsync(item, stopToken).ConfigureAwait(false);
            var resignal = false;
            lock (_sync)
            {
                _runningKeys.Remove(key!);
                _running.Remove(key!);
                if (_queues.TryGetValue(key!, out var queue) && queue.Count > 0)
                {
                    _readyKeys.Enqueue(key!);
                    resignal = true;
                }
                else
                {
                    _queues.Remove(key!);
                }
            }

            if (resignal)
            {
                _ready.Release();
            }
        }
    }

    private (string? Key, WorkItem? Item) TakeReadyItem()
    {
        lock (_sync)
        {
            while (_readyKeys.Count > 0)
            {
                var key = _readyKeys.Dequeue();
                if (!_queues.TryGetValue(key, out var queue) || queue.Count == 0)
                {
                    _queues.Remove(key);
                    continue;
                }

                if (_runningKeys.Contains(key))
                {
                    continue;
                }

                var item = queue.Dequeue();
                _runningKeys.Add(key);
                _running[key] = item;
                _queuedCount--;
                _pluginQueued[item.PluginId] = _pluginQueued.GetValueOrDefault(item.PluginId) - 1;
                return (key, item);
            }
        }

        return (null, null);
    }

    private async Task ExecuteAsync(WorkItem item, CancellationToken stopToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, item.Eviction.Token);
        var outcome = "observed";
        HookProcessResult? result = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            result = await _runner(
                    item.Work.Start,
                    item.Work.Payload,
                    item.Work.Hook.Contribution.Timeout,
                    linked.Token)
                .ConfigureAwait(false);
            outcome = result.Exit switch
            {
                HookProcessExit.Exited when result.ExitCode == 0 => "observed",
                HookProcessExit.TimedOut => "timedOut",
                _ => "failed"
            };
        }
        catch (OperationCanceledException)
        {
            outcome = item.Eviction.IsCancellationRequested ? "evicted" : "cancelled";
        }
        catch (Exception exception)
        {
            outcome = "failed";
            _logger.LogWarning(
                exception,
                "Async hook '{PluginId}/{HookId}' ({EventName}) failed.",
                item.PluginId, item.HookId, item.Work.EventName);
        }

        Complete(item, outcome, result?.FailureDetail, result);
    }

    private void Complete(WorkItem item, string outcome, string? detail, HookProcessResult? result)
    {
        try
        {
            _log.Append(new PluginHookExecutionEntry(
                DateTimeOffset.UtcNow,
                item.PluginId,
                item.HookId,
                item.Work.EventName,
                item.Work.TurnId,
                outcome,
                result?.Duration.TotalMilliseconds ?? 0,
                result?.ExitCode,
                detail,
                result?.StderrTail));
            if (outcome is "failed" or "timedOut")
            {
                _logger.LogWarning(
                    "Async hook '{PluginId}/{HookId}' ({EventName}) ended as {Outcome}: {Detail}",
                    item.PluginId, item.HookId, item.Work.EventName, outcome, detail);
            }
        }
        finally
        {
            item.ReleaseLease();
            item.Completion.TrySetResult();
        }
    }

    private List<WorkItem> DrainQueues()
    {
        var items = _queues.Values.SelectMany(queue => queue).ToList();
        _queues.Clear();
        _readyKeys.Clear();
        _pluginQueued.Clear();
        _queuedCount = 0;
        return items;
    }

    private CancellationTokenSource EvictionSource(string pluginId)
        => _pluginEviction.TryGetValue(pluginId, out var source)
            ? source
            : _pluginEviction[pluginId] = new CancellationTokenSource();

    private static string Key(ResolvedPluginHook hook) => Key(hook.PluginId, hook.Contribution.Id);

    private static string Key(string pluginId, string hookId) => pluginId + "\n" + hookId;

    private static string KeyPlugin(string key)
        => key[..key.IndexOf('\n')];

    private void LogEntry(AsyncHookWork work, string outcome, TimeSpan duration, int? exitCode, string? detail)
        => _log.Append(new PluginHookExecutionEntry(
            DateTimeOffset.UtcNow,
            work.Hook.PluginId,
            work.Hook.Contribution.Id,
            work.EventName,
            work.TurnId,
            outcome,
            duration.TotalMilliseconds,
            exitCode,
            detail,
            null));

    private sealed class WorkItem
    {
        private int _released;

        public WorkItem(AsyncHookWork work, IDisposable lease, CancellationTokenSource eviction)
        {
            Work = work;
            Lease = lease;
            Eviction = eviction;
        }

        public AsyncHookWork Work { get; }

        public IDisposable Lease { get; }

        public CancellationTokenSource Eviction { get; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PluginId => Work.Hook.PluginId;

        public string HookId => Work.Hook.Contribution.Id;

        public void ReleaseLease()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Lease.Dispose();
            }
        }
    }
}
