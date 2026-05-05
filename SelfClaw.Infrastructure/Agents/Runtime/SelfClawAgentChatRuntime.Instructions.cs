using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private static string BuildProgrammingInstructions(ChatTurnRequest request)
    {
        if (request.Mode == ConversationMode.Channel)
        {
            return request.WorkspaceRoot is null
                ? ChannelBaseInstructions + " No workspace is currently selected, so do not mention workspace tools."
                : ChannelBaseInstructions +
                  $" The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Use workspace tools only when they materially help answer the external user.";
        }

        if (request.WorkspaceRoot is null)
        {
            var fallbackInstructions = ProgrammingBaseInstructions + " No workspace is currently selected, so do not mention workspace tools.";
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
        if (!string.IsNullOrWhiteSpace(request.Agent.Instructions))
        {
            instructions += $"\n\nAdditional agent instructions:\n{request.Agent.Instructions.Trim()}";
        }

        return instructions;
    }


    private static string BuildExecutionPlanInstructions(ChatTurnRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Draft a compact execution plan before doing any work.");
        builder.AppendLine("Return JSON only. Do not use Markdown fences.");
        builder.AppendLine("Schema:");
        builder.AppendLine("{\"summary\":\"string\",\"steps\":[{\"id\":\"string\",\"title\":\"string\"}]}");
        builder.AppendLine("Rules:");
        builder.AppendLine($"- Plan between {MinExecutionPlanSteps} and {MaxExecutionPlanSteps} steps.");
        builder.AppendLine("- Keep each step title short, concrete, and action-oriented.");
        builder.AppendLine("- Focus on the actual work needed for this request, not generic process overhead.");
        builder.AppendLine("- Use workspace inspection tools only when they help shape a more accurate plan.");
        builder.AppendLine("- Do not include a final user-facing answer inside the plan.");
        if (!string.IsNullOrWhiteSpace(request.Agent.Instructions))
        {
            builder.AppendLine("- Follow these additional agent instructions when they help:");
            builder.AppendLine(request.Agent.Instructions.Trim());
        }
        if (request.WorkspaceRoot is null)
        {
            builder.AppendLine("- No workspace is selected, so plan around reasoning and chat-only execution.");
        }
        else
        {
            builder.AppendLine($"- The trusted workspace root is '{request.WorkspaceRoot.RootPath}'.");
            builder.AppendLine("- You may inspect the workspace read-only via list/search/read tools while planning.");
        }

        return builder.ToString();
    }


    private static string BuildExecutionStepInstructions(
        ChatTurnRequest request,
        ExecutionPlan executionPlan,
        ExecutionPlanStep currentStep,
        bool isFinalStep)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildProgrammingInstructions(request));
        builder.AppendLine();
        builder.AppendLine("Execute only the current plan step and stream a normal assistant response for this step.");
        builder.AppendLine($"Current plan step: {currentStep.Title}");
        builder.AppendLine($"Total steps in this plan: {executionPlan.Steps.Count}.");
        builder.AppendLine("Respond in Markdown.");
        builder.AppendLine("Your output for this step will be shown directly in the chat transcript, including any reasoning or tool-driven progress supported by the model.");
        builder.AppendLine("Stay focused on the current step, avoid repeating the full recap, and mention concrete findings or actions.");
        if (isFinalStep)
        {
            builder.AppendLine("Because this is the final step, end with the concrete user-facing conclusion or next action.");
        }
        else
        {
            builder.AppendLine("Because later steps still remain, do not present this as the final wrap-up.");
        }

        return builder.ToString();
    }

}
