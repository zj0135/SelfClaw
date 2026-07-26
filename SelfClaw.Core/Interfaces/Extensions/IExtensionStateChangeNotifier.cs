namespace SelfClaw.Core.Interfaces;

public interface IExtensionStateChangeNotifier
{
    long CurrentRevision { get; }

    event Action<long>? StateChanged;

    long Advance();

    long AdvanceTo(long revision);
}
