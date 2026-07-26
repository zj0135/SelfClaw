using System.Windows;
using System.Windows.Threading;

namespace SelfClaw.Desktop.Pet;

internal sealed class DispatcherPetPresentationScheduler : IPetPresentationScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Action? _callback;
    private bool _disposed;

    public DispatcherPetPresentationScheduler()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _timer.Tick += OnTimerTick;
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RunOnDispatcher(() =>
        {
            _timer.Stop();
            _callback = callback;
            _timer.Interval = delay;
            _timer.Start();
        });
    }

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            _timer.Stop();
            _callback = null;
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
        RunOnDispatcher(() => _timer.Tick -= OnTimerTick);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }

}
