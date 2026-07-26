using System.Windows.Media;
using System.Windows.Threading;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// UI-thread frame driver for a spritesheet row.
/// </summary>
public sealed class SpriteAnimator : IDisposable
{
    private readonly SpriteSheet _spriteSheet;
    private readonly DispatcherTimer _timer;
    private string _rowId;
    private int _frameIndex;
    private bool _running;
    private bool _disposed;

    public SpriteAnimator(SpriteSheet spriteSheet, string initialRowId)
    {
        _spriteSheet = spriteSheet;
        _rowId = initialRowId;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    public event Action<ImageSource>? FrameChanged;

    public void Start()
    {
        ThrowIfDisposed();
        _running = true;
        PublishFrame();
        ConfigureTimer();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    public void SetRow(string rowId)
    {
        ThrowIfDisposed();
        if (string.Equals(_rowId, rowId, StringComparison.Ordinal))
        {
            return;
        }

        _rowId = rowId;
        _frameIndex = 0;
        PublishFrame();
        ConfigureTimer();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var row = _spriteSheet.GetRow(_rowId);
        _frameIndex = (_frameIndex + 1) % row.Frames;
        PublishFrame();
    }

    private void ConfigureTimer()
    {
        _timer.Stop();
        if (!_running)
        {
            return;
        }

        var row = _spriteSheet.GetRow(_rowId);
        if (row.Frames <= 1)
        {
            return;
        }

        var intervalMs = Math.Max(16d, 1000d / row.Fps);
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.Start();
    }

    private void PublishFrame()
    {
        FrameChanged?.Invoke(_spriteSheet.GetFrame(_rowId, _frameIndex));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

}
