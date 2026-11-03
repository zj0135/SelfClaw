using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The single AIFunction binding template for a Direct turn. Every bound tool excludes its result schema and
/// is marshalled into a <see cref="DirectToolResult"/> here, so the invoker's result type is guaranteed by one
/// place instead of by each toolset remembering to check what it returns.
/// </summary>
internal static class DirectToolBindings
{
    /// <summary>
    /// Binds a delegate whose result is shaped into a <see cref="DirectToolResult"/> by <paramref name="format"/>.
    /// </summary>
    internal static DirectToolBinding Create<TResult>(
        Delegate method,
        string name,
        string description,
        ToolCallKind kind,
        ToolSourceKind sourceKind,
        Func<TResult, DirectToolResult> format,
        string? displayName = null,
        bool requiresApproval = false)
        => new(AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => value is TResult result
                ? ValueTask.FromResult<object?>(format(result))
                : throw new InvalidOperationException($"Tool '{name}' returned an invalid result.")
        }), new DirectToolDescriptor(kind, sourceKind, DisplayName: displayName), requiresApproval);

    /// <summary>
    /// Binds a delegate that already returns a <see cref="DirectToolResult"/>; the type check is what makes
    /// that part of the contract rather than an assumption.
    /// </summary>
    internal static DirectToolBinding Create(
        Delegate method,
        string name,
        string description,
        ToolCallKind kind,
        ToolSourceKind sourceKind,
        string? displayName = null,
        bool requiresApproval = false)
        => Create<DirectToolResult>(method, name, description, kind, sourceKind, result => result, displayName,
            requiresApproval);
}
