using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Terminal;

public sealed class TerminalHostController : IDisposable
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

    internal TerminalHostController(
        ITerminalSessionFactory sessionFactory,
        WebViewHostChannel webViewHostChannel,
        Dispatcher dispatcher)
    {
        _sessionFactory = sessionFactory;
        _webViewHostChannel = webViewHostChannel;
        _dispatcher = dispatcher;
    }

    public bool IsOpen { get; private set; }

    public bool TryHandleMessage(string type, JsonElement payload)
    {
        switch (type)
        {
            case "terminal-ready":
                _isReady = true;
                ApplyResize(payload);
                PublishState();
                if (IsOpen)
                {
                    EnsureSession();
                }
                return true;
            case "terminal-input":
                if (_session is not null && payload.TryGetProperty("data", out var dataElement))
                {
                    _session.WriteInput(dataElement.GetString() ?? string.Empty);
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
                SetOpen(false, workspaceRootPath: null);
                return true;
            case "terminal-restart":
                RestartSession();
                return true;
            default:
                return false;
        }
    }

    public void SetOpen(bool isOpen, string? workspaceRootPath)
    {
        IsOpen = isOpen;
        if (!isOpen)
        {
            _isFocused = false;
            PublishState();
            return;
        }

        UpdateWorkingDirectory(workspaceRootPath);
        EnsureSession();
        PublishState();
    }

    public void UpdateWorkspaceRoot(string? workspaceRootPath)
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
        StopSession();
        EnsureSession();
        PublishState();
    }

    public bool TryWriteEscape()
    {
        if (!_isFocused || _session is null)
        {
            return false;
        }

        _session.WriteInput("\x1b");
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

    public void Dispose() => StopSession();

    private void UpdateWorkingDirectory(string? workspaceRootPath)
    {
        var nextWorkingDirectory = ResolveWorkingDirectory(workspaceRootPath);
        if (PathsEqual(_workingDirectory, nextWorkingDirectory))
        {
            return;
        }

        _workingDirectory = nextWorkingDirectory;
        StopSession();
    }

    private void EnsureSession()
    {
        if (!_isReady || _session is not null)
        {
            return;
        }

        try
        {
            var session = _sessionFactory.Create(_workingDirectory, _columns, _rows);
            _session = session;
            session.OutputReceived += OnOutputReceived;
            session.Exited += OnExited;
            session.Start();
            PublishState();
            Focus();
        }
        catch (OperationCanceledException)
        {
            StopSession();
            throw;
        }
        catch (Exception exception)
        {
            StopSession();
            PostOutput($"\r\nFailed to start terminal: {exception.Message}\r\n");
        }
    }

    private void RestartSession()
    {
        StopSession();
        _webViewHostChannel.PostPush(new { type = "terminal-clear" });
        if (IsOpen)
        {
            EnsureSession();
        }
    }

    private void StopSession()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnExited;
        session.Dispose();
        _isFocused = false;
        PublishState();
    }

    private void OnOutputReceived(object? sender, string data)
        => _ = _dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ReferenceEquals(sender, _session))
                {
                    PostOutput(data);
                }
            }),
            DispatcherPriority.Background);

    private void OnExited(object? sender, int? exitCode)
        => _ = _dispatcher.BeginInvoke(
            new Action(() => CompleteExitedSession(sender, exitCode)),
            DispatcherPriority.Background);

    private void CompleteExitedSession(object? sender, int? exitCode)
    {
        if (sender is not ITerminalSession exitedSession ||
            !ReferenceEquals(exitedSession, _session))
        {
            return;
        }

        _session = null;
        exitedSession.OutputReceived -= OnOutputReceived;
        exitedSession.Exited -= OnExited;
        exitedSession.Dispose();

        PostOutput(exitCode is int code
            ? $"\r\n[terminal exited with code {code}]\r\n"
            : "\r\n[terminal exited]\r\n");
        PublishState();
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
