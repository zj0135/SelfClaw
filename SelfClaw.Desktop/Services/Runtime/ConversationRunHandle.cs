using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.ConversationInputs;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationRunHandle
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenRegistration _inputCancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ConversationRecord?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConversationRuntimeState? _runtimeState;
    private ConversationTurnExecutionResult? _executionResult;
    private int _executionStarted;
    private int _completed;

    internal ConversationRunHandle(Guid conversationId, AgentExecutionMode mode, DirectTurnOrigin origin,
        long generation, DirectTurnInputSession? inputSession, Guid runId, Guid turnId, CancellationToken cancellationToken)
    {
        ConversationId = conversationId;
        Mode = mode;
        Origin = origin;
        Generation = generation;
        RunId = runId;
        TurnId = turnId;
        InputSession = inputSession;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken = _cancellation.Token;
        if (inputSession is not null)
            _inputCancellation = CancellationToken.Register(() => inputSession.Cancel(CancellationToken));
    }

    public Guid RunId { get; }
    public Guid ConversationId { get; }
    public Guid TurnId { get; }
    public AgentExecutionMode Mode { get; }
    public DirectTurnOrigin Origin { get; }
    public long Generation { get; }
    public CancellationToken CancellationToken { get; }
    public DirectTurnInputSession? InputSession { get; }
    public ConversationRuntimeState? RuntimeState => Volatile.Read(ref _runtimeState);
    public Task Completion => _completion.Task;
    public Task<ConversationRecord?> Started => _started.Task;
    internal ConversationTurnExecutionResult? ExecutionResult => Volatile.Read(ref _executionResult);

    internal void SetExecutionResult(ConversationTurnExecutionResult result)
    {
        if (Interlocked.CompareExchange(ref _executionResult, result, null) is not null)
            throw new InvalidOperationException("The execution outcome has already been recorded.");
    }

    internal void AttachState(ConversationRuntimeState state)
    {
        if (Interlocked.CompareExchange(ref _runtimeState, state, null) is not null)
            throw new InvalidOperationException("The run already owns a transcript state.");
        _started.TrySetResult(state.Conversation);
    }

    internal void BeginExecution()
    {
        if (Interlocked.Exchange(ref _executionStarted, 1) != 0)
            throw new InvalidOperationException("This run handle has already been executed.");
    }

    internal void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal bool IsCompleting => Volatile.Read(ref _completed) != 0;

    internal bool TryBeginCompletion() => Interlocked.Exchange(ref _completed, 1) == 0;

    internal void ReleaseResources()
    {
        InputSession?.Close();
        _inputCancellation.Dispose();
        _cancellation.Dispose();
    }

    internal void MarkCompleted()
    {
        _started.TrySetResult(null);
        _completion.TrySetResult();
    }
}
