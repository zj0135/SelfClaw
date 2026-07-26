using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

internal sealed class ManualPetPresentationScheduler : IPetPresentationScheduler
{
    private Action? _callback;

    public TimeSpan? Delay { get; private set; }

    public bool IsScheduled => _callback is not null;

    public void Schedule(TimeSpan delay, Action callback)
    {
        Delay = delay;
        _callback = callback;
    }

    public void Cancel()
    {
        Delay = null;
        _callback = null;
    }

    public void Fire()
    {
        var callback = _callback;
        _callback = null;
        Delay = null;
        callback?.Invoke();
    }

    public void Dispose() => Cancel();
}
