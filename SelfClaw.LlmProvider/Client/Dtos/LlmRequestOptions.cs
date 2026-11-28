using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SelfClaw.LlmProvider.Client.Dtos;

/// <summary>一次调用可选的运行时输入。</summary>
/// <param name="Tools">这一回合可用的工具；为空表示不带工具。</param>
/// <param name="LoggerFactory">可选。本库不强制任何日志策略，也不替调用方屏蔽任何级别。</param>
/// <param name="StreamIdleTimeout">流式响应两次更新之间的最长静默；超时按失败处理。</param>
public sealed record LlmRequestOptions(
    IReadOnlyList<AITool>? Tools = null,
    ILoggerFactory? LoggerFactory = null,
    TimeSpan? StreamIdleTimeout = null)
{
    public static LlmRequestOptions Empty { get; } = new();
}
