namespace SelfClaw.Tests.TestDoubles;

internal sealed class TriggeredTimeoutTimeProvider : TimeProvider
{
    private TimerCallback? _callback;
    private object? _state;

    internal TimeSpan DueTime { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _state = state;
        DueTime = dueTime;
        return System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void TriggerTimeout()
    {
        var callback = _callback ?? throw new InvalidOperationException("No timeout has been registered.");
        callback(_state);
    }
}
