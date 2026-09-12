using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class DesktopActivityWebViewTests(ITestOutputHelper output)
{
    [DesktopSmokeFact]
    public Task Real_WebView_renders_three_live_children_and_restores_after_navigation()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var activity = new SubagentActivityTestContext();
            var first = await activity.CreateTaskAsync();
            var parent = await activity.Conversations.GetConversationAsync(first.ParentConversationId) ?? throw new InvalidOperationException();
            var tasks = new[] { first, await activity.CreateTaskAsync(parent), await activity.CreateTaskAsync(parent) };
            var runtimes = tasks.Select(_ => new ControlledSubagentRuntime()).ToArray();
            using var shutdown = new CancellationTokenSource();
            var executions = tasks.Select((task, index) => activity.CreateExecutor(task, runtimes[index]).ExecuteAsync(task, shutdown.Token)).ToArray();
            await Task.WhenAll(runtimes.Select(runtime => runtime.Started.Task));
            var channel = new WebViewHostChannel();
            var source = new ActivityPanelTestScope(parent.Id);
            using var publisher = new ActivityPanelPublisher(activity.Service,
                new ActivityPanelSnapshotBuilder(activity.Service, StoragePaths.CreateDefault()), source, channel,
                Dispatcher.CurrentDispatcher, NullLogger<ActivityPanelPublisher>.Instance);
            var bridge = new ActivityPanelBridge(publisher, activity.Service, activity.CreateCoordinator(), channel, Dispatcher.CurrentDispatcher);
            using var webView = new WebView2();
            var window = new Window { Content = webView, Width = 1280, Height = 800, ShowInTaskbar = false, ShowActivated = false, Title = "SelfClaw activity verification" };
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
            var artifacts = Path.Combine(root, "SelfClaw.Tests", "TestResults", "activity-webview");
            Directory.CreateDirectory(artifacts);
            try
            {
                window.Show();
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(artifacts, "webview-profile"));
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(WebViewMessageRouter.ApplicationHostName,
                    Path.Combine(root, "SelfClaw.Desktop", "Assets", "TranscriptVue"), CoreWebView2HostResourceAccessKind.DenyCors);
                channel.Attach(webView.CoreWebView2.PostWebMessageAsJson);
                webView.NavigationStarting += (_, _) => channel.MarkNotReady();
                webView.NavigationCompleted += (_, args) => { if (args.IsSuccess) channel.MarkReady(); };
                var routeErrors = new List<string>();
                webView.CoreWebView2.WebMessageReceived += async (_, args) =>
                {
                    try
                    {
                        if (!WebViewMessageRouter.IsApplicationOrigin(args.Source)) return;
                        using var document = JsonDocument.Parse(args.WebMessageAsJson);
                        var payload = document.RootElement;
                        var type = payload.GetProperty("type").GetString() ?? string.Empty;
                        if (type.StartsWith("activity-panel/", StringComparison.Ordinal)) await bridge.HandleAsync(type, payload, CancellationToken.None);
                        else if (type == "transcript-rendered") channel.AcknowledgeTranscript(payload.GetProperty("revision").GetInt64());
                        else if (payload.TryGetProperty("requestId", out var requestId))
                            channel.PostResponse(new { type, requestId = requestId.GetString(), tabs = Array.Empty<object>(), panels = Array.Empty<object>(), models = Array.Empty<object>(), agents = Array.Empty<object>(), roots = Array.Empty<object>() });
                    }
                    catch (Exception exception) { routeErrors.Add(exception.GetType().Name + ": " + exception.Message); }
                };
                channel.PublishTranscript(new TranscriptRenderState([], false, [], parent.Id.ToString("D"), false));
                webView.Source = new Uri($"https://{WebViewMessageRouter.ApplicationHostName}/index.html");
                await WaitForScriptAsync(webView, "document.querySelectorAll('.task-row').length === 3");
                await webView.ExecuteScriptAsync("document.querySelector('.task-select').click()");
                for (var index = 0; index < 3; index++)
                {
                    await runtimes[index].EmitAsync(new AssistantThinkingDeltaEvent("thinking", $"visible child {index}"));
                    await runtimes[index].EmitAsync(new ToolCallStartedEvent("same-call", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
                }
                await WaitForScriptAsync(webView, "Boolean(document.querySelector('.task-detail .thinking-summary'))");
                await webView.ExecuteScriptAsync("document.querySelector('.task-detail .thinking-summary').click()");
                await WaitForScriptAsync(webView, "document.querySelector('.thinking-content')?.textContent.includes('visible child 0') === true");
                executions.Should().OnlyContain(execution => !execution.IsCompleted);
                await MeasureLiveRenderingAsync(webView, runtimes[0]);
                await using (var screenshot = File.Create(Path.Combine(artifacts, "live.png")))
                    await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                var approvalId = Guid.NewGuid();
                var approval = activity.Approvals.RequestApprovalAsync(new ToolApprovalRequest(approvalId,
                    "write_file", "Write fixture", "Verification", "{}", tasks[0].ChildConversationId));
                await WaitForScriptAsync(webView, "document.querySelector('.task-row')?.textContent.includes('等待审批') === true");
                webView.Reload();
                await WaitForScriptAsync(webView, "document.querySelectorAll('.task-row').length === 3");
                await WaitForScriptAsync(webView, "document.querySelector('.task-row')?.textContent.includes('等待审批') === true");
                activity.Approvals.TryResolve(approvalId, approved: false).Should().BeTrue();
                (await approval).Should().BeFalse();
                await webView.ExecuteScriptAsync("document.querySelector('.task-select').click()");
                await WaitForScriptAsync(webView, "Boolean(document.querySelector('.thinking-summary'))");
                await runtimes[0].EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "result 0"));
                await executions[0];
                await WaitForScriptAsync(webView, "document.querySelectorAll('.task-row.succeeded').length === 1");
                await webView.ExecuteScriptAsync("document.querySelector('.cancel-task').click()");
                await WaitForScriptAsync(webView, "document.querySelectorAll('.task-row.cancelled').length === 1");
                await shutdown.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executions[1]);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executions[2]);
                await WaitForScriptAsync(webView, "document.querySelectorAll('.task-row.cancelled').length === 2");
                await using (var screenshot = File.Create(Path.Combine(artifacts, "terminal.png")))
                    await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                routeErrors.Should().BeEmpty();
                output.WriteLine("Parent={0}; Tasks={1}; WebView2={2}; Artifacts={3}", parent.Id,
                    string.Join(',', tasks.Select(task => task.Id)), environment.BrowserVersionString, artifacts);
            }
            finally
            {
                channel.Detach();
                window.Close();
                await shutdown.CancelAsync();
                try { await Task.WhenAll(executions); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            }
        }, 90);

    private async Task MeasureLiveRenderingAsync(WebView2 webView, ControlledSubagentRuntime runtime)
    {
        var samples = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            var marker = $"visible-token-{index:D2}";
            var timer = Stopwatch.StartNew();
            await runtime.EmitAsync(new AssistantTextDeltaEvent("text", $" {marker}"));
            await WaitForScriptAsync(webView,
                $"document.querySelector('.task-detail .body-segment')?.textContent.includes('{marker}') === true");
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }

        var p95 = samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1);
        output.WriteLine("WebViewRenderSamples={0}; TextRenderP95Ms={1:F1}", samples.Count, p95);
        p95.Should().BeLessThan(500, "the visible WebView should render the cumulative child text within the P6 target");
    }

    private static async Task WaitForScriptAsync(WebView2 webView, string expression)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await webView.ExecuteScriptAsync(expression) != "true") await Task.Delay(50, timeout.Token);
    }
}
