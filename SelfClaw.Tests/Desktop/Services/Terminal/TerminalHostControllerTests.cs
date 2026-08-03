using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Tests.Desktop.Services.Terminal;

public sealed class TerminalHostControllerTests
{
    [Fact]
    public void Ready_open_input_resize_focus_and_dispose_share_one_lifecycle()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);
        channel.MarkReady();
        var factory = new FakeTerminalSessionFactory();
        var controller = new TerminalHostController(factory, channel, Dispatcher.CurrentDispatcher);
        using var ready = JsonDocument.Parse("""
            { "cols": 80, "rows": 30 }
            """);

        controller.TryHandleMessage("terminal-ready", ready.RootElement).Should().BeTrue();
        controller.SetOpen(true, Path.GetTempPath());

        var session = factory.Sessions.Should().ContainSingle().Subject;
        session.Started.Should().BeTrue();
        factory.Columns.Should().Be(80);
        factory.Rows.Should().Be(30);

        using var input = JsonDocument.Parse("""
            { "data": "dir\r" }
            """);
        controller.TryHandleMessage("terminal-input", input.RootElement).Should().BeTrue();
        session.Inputs.Should().ContainSingle().Which.Should().Be("dir\r");

        using var resize = JsonDocument.Parse("""
            { "cols": 100, "rows": 40 }
            """);
        controller.TryHandleMessage("terminal-resize", resize.RootElement).Should().BeTrue();
        session.LastSize.Should().Be((100, 40));

        using var focus = JsonDocument.Parse("""
            { "isFocused": true }
            """);
        controller.TryHandleMessage("terminal-focus-change", focus.RootElement).Should().BeTrue();
        controller.TryWriteEscape().Should().BeTrue();
        session.Inputs.Should().EndWith("\x1b");

        controller.SetOpen(false, workspaceRootPath: null);
        session.Disposed.Should().BeFalse("closing the drawer keeps the shell session alive");

        controller.Dispose();
        session.Disposed.Should().BeTrue();
        messages.Should().Contain(message => message.Contains("terminal-state", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_failure_disposes_the_failed_session_and_restart_can_create_another()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);
        channel.MarkReady();
        var factory = new FakeTerminalSessionFactory
        {
            NextStartException = new InvalidOperationException("start failed")
        };
        var controller = new TerminalHostController(factory, channel, Dispatcher.CurrentDispatcher);
        using var payload = JsonDocument.Parse("{}");

        controller.TryHandleMessage("terminal-ready", payload.RootElement).Should().BeTrue();
        controller.SetOpen(true, Path.GetTempPath());

        factory.Sessions.Should().ContainSingle()
            .Which.Disposed.Should().BeTrue();
        messages.Should().Contain(message => message.Contains("Failed to start terminal: start failed", StringComparison.Ordinal));

        controller.TryHandleMessage("terminal-restart", payload.RootElement).Should().BeTrue();

        factory.Sessions.Should().HaveCount(2);
        factory.Sessions[1].Started.Should().BeTrue();
        factory.Sessions[1].Disposed.Should().BeFalse();
        controller.Dispose();
    }

    private sealed class FakeTerminalSessionFactory : ITerminalSessionFactory
    {
        public List<FakeTerminalSession> Sessions { get; } = [];

        public int Columns { get; private set; }

        public int Rows { get; private set; }

        public Exception? NextStartException { get; set; }

        public ITerminalSession Create(string workingDirectory, int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            var session = new FakeTerminalSession(NextStartException);
            NextStartException = null;
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly Exception? _startException;

        public FakeTerminalSession(Exception? startException = null)
        {
            _startException = startException;
        }

        public event EventHandler<string>? OutputReceived;

        public event EventHandler<int?>? Exited;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> Inputs { get; } = [];

        public (int Columns, int Rows) LastSize { get; private set; }

        public void Start()
        {
            if (_startException is not null)
            {
                throw _startException;
            }

            Started = true;
        }

        public void WriteInput(string input) => Inputs.Add(input);

        public void Resize(int columns, int rows) => LastSize = (columns, rows);

        public void Dispose() => Disposed = true;

        public void PublishOutput(string output) => OutputReceived?.Invoke(this, output);

        public void PublishExit(int? exitCode) => Exited?.Invoke(this, exitCode);
    }
}
