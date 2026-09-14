using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Threading;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Terminal;

public sealed class TerminalHostController : IDisposable, IAsyncDisposable
{
    private const int DefaultColumns = 120;
    private const int DefaultRows = 24;

    private readonly ITerminalSessionFactory _sessionFactory;
    private readonly WebViewHostChannel _webViewHostChannel;
    private readonly Dispatcher _dispatcher;
    private ITerminalSession? _session;
    private string _workingDirectory = ResolveDefaultWorkingDirectory();
    private int _columns = DefaultColumns;
    private int _rows = DefaultRows;
    private bool _isReady;
    private bool _isFocused;
    private readonly TerminalOutputBuffer _output = new();
    private readonly DispatcherTimer _outputTimer;
    private readonly ILogger<TerminalHostController> _logger;
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private int _pendingInputs;
    private bool _disposed;
    private Task? _disposeTask;


    internal TerminalHostController(
        ITerminalSessionFactory sessionFactory,
        WebViewHostChannel webViewHostChannel,
        Dispatcher dispatcher,
        ILogger<TerminalHostController>? logger = null)
    {
        _sessionFactory = sessionFactory;
        _webViewHostChannel = webViewHostChannel;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger<TerminalHostController>.Instance;
        _outputTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _outputTimer.Tick += OnOutputTick;
        _webViewHostChannel.ReadyChanged += OnReadyChanged;
    }

    public bool IsOpen { get; private set; }

    public async Task<bool> TryHandleMessageAsync(string type, JsonElement payload)
    {
        switch (type)
        {
            case "terminal-ready":
                _isReady = true;
                ApplyResize(payload);
                PublishState();
                if (IsOpen)
                {
                    await EnsureSessionAsync();
                }
                return true;
            case "terminal-input":
                if (_session is not null && payload.TryGetProperty("data", out var dataElement))
                {
                    await WriteInputAsync(dataElement.GetString() ?? string.Empty);
                }
                return true;
            case "terminal-resize":
                ApplyResize(payload);
                return true;
            case "terminal-focus-change":
                _isFocused = payload.TryGetProperty("isFocused", out var focusedElement) &&
                             focusedElement.GetBoolean() &&
                             IsOpen;
                return true;
            case "terminal-close":
                await SetOpenAsync(false, workspaceRootPath: null);
                return true;
            case "terminal-restart":
                await RestartSessionAsync();
                return true;
            default:
                return false;
        }
    }

    public async Task SetOpenAsync(bool isOpen, string? workspaceRootPath)
    {
        await _transitions.WaitAsync();
        try
        {
            IsOpen = isOpen;
            if (!isOpen)
            {
                _isFocused = false;
                PublishState();
                return;
            }

            await UpdateWorkingDirectoryAsync(workspaceRootPath);
            await EnsureSessionAsync();
            PublishState();
        }
        finally { _transitions.Release(); }
    }

    public async Task UpdateWorkspaceRootAsync(string? workspaceRootPath)
    {
        await _transitions.WaitAsync();
        try
        {
            if (!IsOpen)
            {
                return;
            }

            var nextWorkingDirectory = ResolveWorkingDirectory(workspaceRootPath);
            if (PathsEqual(_workingDirectory, nextWorkingDirectory))
            {
                return;
            }

            _workingDirectory = nextWorkingDirectory;
            await StopSessionAsync();
            await EnsureSessionAsync();
            PublishState();
        }
        finally { _transitions.Release(); }
    }

    public async Task<bool> TryWriteEscapeAsync()
    {
        if (!_isFocused || _session is null)
        {
            return false;
        }

        await WriteInputAsync("\x1b");
        return true;
    }

    public void PublishState()
        => _webViewHostChannel.PostPush(new
        {
            type = "terminal-state",
            isOpen = IsOpen,
            isRunning = _session is not null,
            cwd = _workingDirectory
        });

    public void Focus()
        => _webViewHostChannel.PostPush(new { type = "terminal-focus" });

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _outputTimer.Stop();
        _outputTimer.Tick -= OnOutputTick;
        _webViewHostChannel.ReadyChanged -= OnReadyChanged;
        await _transitions.WaitAsync();
        try { await StopSessionAsync(); }
        finally { _transitions.Release(); }
    }

    private async Task WriteInputAsync(string input)
    {
        if (input.Length > 65536) throw new ArgumentException("Terminal input exceeds 64 KiB of characters.");
        if (Interlocked.Increment(ref _pendingInputs) > 32)
        {
            Interlocked.Decrement(ref _pendingInputs);
            throw new InvalidOperationException("Terminal input is busy; please retry.");
        }
        try
        {
            var session = _session;
            if (session is not null) await session.WriteInputAsync(input);
        }
        finally { Interlocked.Decrement(ref _pendingInputs); }
    }

    private void OnReadyChanged(bool ready)
    {
        if (!ready) { _isReady = false; _isFocused = false; }
    }

    private void OnOutputTick(object? sender, EventArgs e) => FlushOutput();

    internal void FlushOutput()
    {
        if (_disposed || !IsOpen || !_isReady || !_webViewHostChannel.IsReady) return;
        var text = _output.Drain();
        if (text.Length > 0) PostOutput(text);
    }

    private async Task UpdateWorkingDirectoryAsync(string? workspaceRootPath)
    {
        var nextWorkingDirectory = ResolveWorkingDirectory(workspaceRootPath);
        if (PathsEqual(_workingDirectory, nextWorkingDirectory))
        {
            return;
        }

        _workingDirectory = nextWorkingDirectory;
        await StopSessionAsync();
    }

    private async Task EnsureSessionAsync()
    {
        if (_disposed || !_isReady || _session is not null)
        {
            return;
        }

        try
        {
            var session = _sessionFactory.Create(_workingDirectory, _columns, _rows);
            _session = session;
            _output.Reset(session);
            _outputTimer.Start();
            session.OutputReceived += OnOutputReceived;
            session.Exited += OnExited;
            session.Start();
            PublishState();
            Focus();
        }
        catch (OperationCanceledException)
        {
            await StopSessionAsync();
            throw;
        }
        catch (Exception exception)
        {
            await StopSessionAsync();
            PostOutput($"\r\nFailed to start terminal: {exception.Message}\r\n");
        }
    }

    private async Task RestartSessionAsync()
    {
        await _transitions.WaitAsync();
        try
        {
            await StopSessionAsync();
            _webViewHostChannel.PostPush(new { type = "terminal-clear" });
            if (IsOpen)
            {
                await EnsureSessionAsync();
            }
        }
        finally { _transitions.Release(); }
    }

    private async Task StopSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnExited;
        await session.DisposeAsync();
        _isFocused = false;
        PublishState();
    }

    private void OnOutputReceived(object? sender, string data) => _output.Append(sender, data);

    private void OnExited(object? sender, int? exitCode)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.InvokeAsync(async () =>
        {
            try { await CompleteExitedSessionAsync(sender, exitCode); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Terminal exit cleanup failed."); }
        });
    }

    private async Task CompleteExitedSessionAsync(object? sender, int? exitCode)
    {
        await _transitions.WaitAsync();
        try
        {
            if (sender is not ITerminalSession exited || !ReferenceEquals(exited, _session)) return;
            _output.Append(sender, exitCode is int code ? $"\r\n[terminal exited with code {code}]\r\n" : "\r\n[terminal exited]\r\n");
            FlushOutput();
            await StopSessionAsync();
        }
        finally { _transitions.Release(); }
    }

    private void ApplyResize(JsonElement payload)
    {
        if (!payload.TryGetProperty("cols", out var columnsElement) ||
            !payload.TryGetProperty("rows", out var rowsElement))
        {
            return;
        }

        _columns = Math.Max(1, columnsElement.GetInt32());
        _rows = Math.Max(1, rowsElement.GetInt32());
        _session?.Resize(_columns, _rows);
    }

    private void PostOutput(string data)
        => _webViewHostChannel.PostPush(new
        {
            type = "terminal-output",
            data
        });

    private static string ResolveWorkingDirectory(string? workspaceRootPath)
        => !string.IsNullOrWhiteSpace(workspaceRootPath) && Directory.Exists(workspaceRootPath)
            ? workspaceRootPath
            : ResolveDefaultWorkingDirectory();

    private static string ResolveDefaultWorkingDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath) && Directory.Exists(desktopPath))
        {
            return desktopPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? AppContext.BaseDirectory : userProfile;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
