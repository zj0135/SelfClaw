namespace SelfClaw.Desktop.Pet;

internal interface IPetPresentationScheduler : IDisposable
{
    void Schedule(TimeSpan delay, Action callback);

    void Cancel();
}
