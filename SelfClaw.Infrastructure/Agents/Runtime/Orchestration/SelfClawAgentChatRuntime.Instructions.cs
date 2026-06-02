using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime.Orchestration;

public sealed partial class SelfClawAgentChatRuntime
{
    private static string BuildProgrammingInstructions(ChatTurnRequest request)
    {
        if (request.WorkspaceRoot is null)
        {
            var fallbackInstructions = ProgrammingBaseInstructions + " No workspace is currently selected, so do not mention workspace tools.";
            AppendCapabilityInstructions(request, ref fallbackInstructions);
            if (!string.IsNullOrWhiteSpace(request.Agent.Instructions))
            {
                fallbackInstructions += $"\n\nAdditional agent instructions:\n{request.Agent.Instructions.Trim()}";
            }

            return fallbackInstructions;
        }

        var permissionInstructions = request.ToolPermissionMode == ToolPermissionMode.FullAccess
            ? " You may use file-writing and PowerShell tools without extra approval, but stay scoped to the selected workspace unless the user explicitly requests otherwise."
            : " File-writing and PowerShell tools require explicit user approval. Only call them when they are necessary, and keep commands narrowly scoped.";

        var instructions = ProgrammingBaseInstructions +
                           $" The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Keep file references relative to that root." +
                           permissionInstructions;
        AppendCapabilityInstructions(request, ref instructions);
        if (!string.IsNullOrWhiteSpace(request.Agent.Instructions))
        {
            instructions += $"\n\nAdditional agent instructions:\n{request.Agent.Instructions.Trim()}";
        }

        return instructions;
    }

    private static void AppendCapabilityInstructions(ChatTurnRequest request, ref string instructions)
    {
        var capabilityInstructions = BuildCapabilityInstructions(request);
        if (!string.IsNullOrWhiteSpace(capabilityInstructions))
        {
            instructions += capabilityInstructions;
        }
    }

    private static string BuildCapabilityInstructions(ChatTurnRequest request)
    {
        var sections = new List<string>();

        if (request.Agent.Skills.Count > 0)
        {
            sections.Add($" Enabled skills: {string.Join(", ", request.Agent.Skills)}. Use their instructions and resources when relevant.");
        }

        if (request.Agent.ConfiguredMcpServers.Count > 0)
        {
            var serverLabels = request.Agent.ConfiguredMcpServers
                .Select(item => item.EffectiveDisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            sections.Add($" Enabled MCP servers: {string.Join(", ", serverLabels)}. Their tools are available during this turn when they materially help.");
        }

        return sections.Count == 0
            ? string.Empty
            : string.Concat(sections);
    }
}
