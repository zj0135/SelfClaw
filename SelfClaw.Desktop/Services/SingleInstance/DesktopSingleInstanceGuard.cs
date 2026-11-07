using System.Diagnostics;
using System.Windows.Threading;

namespace SelfClaw.Desktop.Services.SingleInstance;

/// <summary>
/// 一个用户会话里只允许一个 SelfClaw 主进程。后启动的进程拿不到锁时只做两件事：把已经在跑的那个
/// 窗口叫到前面，然后自己退出——绝不建立第二个宿主（两套 TickerQ 调度、两个 SQLite 写者、两份
/// 托盘图标、两个自动化执行者同时存在的代价远不止多一个窗口）。
/// </summary>
internal sealed class DesktopSingleInstanceGuard : IDisposable
{
    internal const string DefaultInstanceName = "SelfClaw.Desktop";

    // 主实例刚拿到锁到建好命名事件之间有几毫秒空窗，后启动的进程在这段时间里打不开事件；
    // 等一小会儿再判定「对方不是我方」。
    private static readonly TimeSpan SignalOpenTimeout = TimeSpan.FromMilliseconds(300);

    private readonly Dispatcher _dispatcher;
    private readonly string _instanceName;
    private readonly Mutex _mutex;
    private EventWaitHandle? _activationEvent;
    private ManualResetEvent? _stopEvent;
    private Thread? _listener;
    private Action? _onActivationRequested;
    private bool _ownsMutex;
    private int _pendingActivation;
    private int _disposeStarted;

    public DesktopSingleInstanceGuard(Dispatcher dispatcher, string instanceName = DefaultInstanceName)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        _dispatcher = dispatcher;
        _instanceName = instanceName;
        _mutex = new Mutex(initiallyOwned: false, MutexName);
    }

    /// <summary>成为唯一实例。返回 false 表示已经有别的 SelfClaw 主进程拿走了锁。</summary>
    public bool TryAcquire(TimeSpan timeout)
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀或崩溃走的：锁已弃置并转给我们。按「拿到锁」处理，
            // 否则崩溃一次之后 SelfClaw 就再也起不来了。
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    /// <summary>拿到锁之后调用：开始接收「又有人双击了桌面图标」的信号。</summary>
    public void StartListening()
    {
        if (!_ownsMutex || _listener is not null)
        {
            return;
        }

        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var stopEvent = new ManualResetEvent(false);
        _activationEvent = activationEvent;
        _stopEvent = stopEvent;
        _listener = new Thread(() => Listen(activationEvent, stopEvent))
        {
            IsBackground = true,
            Name = "SelfClaw single-instance listener"
        };
        _listener.Start();
    }

    public void SetActivationHandler(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);
        _onActivationRequested = onActivationRequested;

        // 窗口还没建好就有人双击了图标：那次信号先记着，这里补放一次。丢掉的后果是用户看到
        // 「双击没反应」，而且再点一次也不会变好（事件是自动复位的）。
        if (Interlocked.Exchange(ref _pendingActivation, 0) != 0)
        {
            RequestActivation(onActivationRequested);
        }
    }

    /// <summary>在「已有实例」的那一侧调用：置位命名事件，让对方把窗口亮出来。</summary>
    public bool SignalPrimaryInstance()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                return activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                if (stopwatch.Elapsed >= SignalOpenTimeout)
                {
                    return false;
                }

                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                // 对方用不同的完整性级别（例如管理员）在跑：没有权限就安静退出，
                // 尝试强行拉起第二个实例只会绕开单例本身。
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _stopEvent?.Set();
        if (_listener is not null)
        {
            _listener.Join(TimeSpan.FromSeconds(1));
            _listener = null;
        }

        _activationEvent?.Dispose();
        _activationEvent = null;
        _stopEvent?.Dispose();
        _stopEvent = null;

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 释放的不是持有线程（退出路径上换过线程）：让内核随进程退出兜底。
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }

    private string MutexName => $@"Local\{_instanceName}";

    private string ActivationEventName => $@"Local\{_instanceName}.Activate";

    private void Listen(EventWaitHandle activationEvent, ManualResetEvent stopEvent)
    {
        WaitHandle[] handles = [activationEvent, stopEvent];
        while (true)
        {
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // 下标 1 是停止信号（Dispose 置位），其余情况只可能是激活请求。
            if (signaled != 0)
            {
                return;
            }

            NotifyActivation();
        }
    }

    private void NotifyActivation()
    {
        var handler = Volatile.Read(ref _onActivationRequested);
        if (handler is null)
        {
            Interlocked.Exchange(ref _pendingActivation, 1);
            return;
        }

        RequestActivation(handler);
    }

    private void RequestActivation(Action handler)
    {
        // 关闭中收到请求无处可去：别把一个正在拆的窗口又 Show 回来。
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, handler);
        }
        catch (InvalidOperationException)
        {
            // 调度器刚好在这一刻关闭，同上一行。
        }
    }
}
