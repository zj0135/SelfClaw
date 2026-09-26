namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal enum PluginHookEvent
{
    RunStarting,
    RunCompleted,
    ToolExecuting,
    ToolExecuted,
    HttpRequestSending,
    HttpResponseReceived
}
