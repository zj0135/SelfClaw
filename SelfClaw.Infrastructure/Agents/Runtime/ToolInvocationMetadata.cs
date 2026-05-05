using System.Text.Json;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class ToolInvocationMetadata
{
    private const int MaxResultContentLength = 16_000;

    public required bool RequiresApproval { get; init; }

    public required string ApprovalTitle { get; init; }

    public required Func<string, string> BuildApprovalDescription { get; init; }

    public required Func<string, string> BuildApprovalGrantedSummary { get; init; }

    public required Func<string, object?> BuildDeniedResult { get; init; }

    public required Func<object?, string> SummarizeResult { get; init; }

    public required Func<object?, JsonSerializerOptions, string?> DescribeResult { get; init; }

    public static ToolInvocationMetadata Create<T>(
        Func<T, string> summarizeResult,
        Func<T, string>? describeResult = null,
        bool requiresApproval = false,
        string approvalTitle = "",
        Func<string, string>? buildApprovalDescription = null,
        Func<string, string>? buildApprovalGrantedSummary = null,
        Func<string, T>? buildDeniedResult = null)
        => new()
        {
            RequiresApproval = requiresApproval,
            ApprovalTitle = approvalTitle,
            BuildApprovalDescription = buildApprovalDescription ?? (_ => string.Empty),
            BuildApprovalGrantedSummary = buildApprovalGrantedSummary ?? (_ => string.Empty),
            BuildDeniedResult = buildDeniedResult is null
                ? _ => default(T)
                : argumentsJson => buildDeniedResult(argumentsJson),
            SummarizeResult = result => summarizeResult(CastResult<T>(result)),
            DescribeResult = describeResult is null
                ? static (_, _) => null
                : (result, _) => describeResult(CastResult<T>(result))
        };

    public static ToolInvocationMetadata CreateMcp(
        AgentMcpServerDefinition server,
        string originalToolName,
        bool requiresApproval)
        => new()
        {
            RequiresApproval = requiresApproval,
            ApprovalTitle = "Call MCP Tool",
            BuildApprovalDescription = argumentsJson =>
                $"Allow the agent to call MCP tool '{originalToolName}' on server '{server.EffectiveDisplayName}'?{Environment.NewLine}{Environment.NewLine}Arguments:{Environment.NewLine}{argumentsJson}",
            BuildApprovalGrantedSummary = _ =>
                $"Approval granted. Calling MCP tool '{server.EffectiveDisplayName}/{originalToolName}'...",
            BuildDeniedResult = _ => JsonSerializer.SerializeToElement(new
            {
                isError = true,
                error = "User denied approval.",
                server = server.Id,
                tool = originalToolName
            }),
            SummarizeResult = _ => $"Completed MCP tool '{server.EffectiveDisplayName}/{originalToolName}'.",
            DescribeResult = (result, serializerOptions) => SerializeResult(result, serializerOptions)
        };

    public static string SerializeResult(object? result, JsonSerializerOptions serializerOptions)
    {
        if (result is null)
        {
            return string.Empty;
        }

        try
        {
            var content = result switch
            {
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(result, serializerOptions)
            };

            return content.Length <= MaxResultContentLength
                ? content
                : $"{content[..MaxResultContentLength]}...";
        }
        catch
        {
            var content = result.ToString() ?? string.Empty;
            return content.Length <= MaxResultContentLength
                ? content
                : $"{content[..MaxResultContentLength]}...";
        }
    }

    private static T CastResult<T>(object? result)
    {
        if (result is T typed)
        {
            return typed;
        }

        if (result is null)
        {
            return default!;
        }

        throw new InvalidCastException($"Expected tool result of type '{typeof(T).FullName}', but received '{result.GetType().FullName}'.");
    }
}
