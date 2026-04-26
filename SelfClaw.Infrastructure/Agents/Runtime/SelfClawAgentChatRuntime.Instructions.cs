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
            return ProgrammingBaseInstructions + " No workspace is currently selected, so do not mention workspace tools.";
        }

        var permissionInstructions = request.ToolPermissionMode == ToolPermissionMode.FullAccess
            ? " You may use file-writing and PowerShell tools without extra approval, but stay scoped to the selected workspace unless the user explicitly requests otherwise."
            : " File-writing and PowerShell tools require explicit user approval. Only call them when they are necessary, and keep commands narrowly scoped.";

        return ProgrammingBaseInstructions +
               $" The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Keep file references relative to that root." +
               permissionInstructions;
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


    private static string BuildBoundAgentInstructions(ChatTurnRequest request, TeamAgentRecord agent)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You are {agent.Name}, acting as {agent.Role}, in a dedicated follow-up session that branches from a larger team conversation.");
        builder.AppendLine(agent.GoalPrompt);
        builder.AppendLine("Respond in Markdown.");
        builder.AppendLine("Use the inherited main-conversation context as background, then answer the user directly in this branch.");
        builder.AppendLine("Stay in role as this specialist. Do not convert the session back into a coordinator summary unless the user explicitly asks for that.");
        builder.AppendLine("Do not claim tools were used unless tool results were actually returned.");

        if (request.WorkspaceRoot is null)
        {
            builder.AppendLine("No workspace is selected, so rely on the inherited discussion context and the user messages in this branch.");
            return builder.ToString();
        }

        builder.AppendLine($"The trusted workspace root is '{request.WorkspaceRoot.RootPath}'. Keep file references relative to that root.");
        builder.AppendLine(
            request.ToolPermissionMode == ToolPermissionMode.FullAccess
                ? "You may use file-writing and PowerShell tools without extra approval, but stay scoped to the selected workspace unless the user explicitly requests otherwise."
                : "File-writing and PowerShell tools require explicit user approval. Only call them when they are necessary, and keep commands narrowly scoped.");
        return builder.ToString();
    }


    private static string BuildCoordinatorPlanningInstructions(
        WorkspaceRoot? workspaceRoot,
        IReadOnlyList<TeamAgentRecord> existingTeamAgents,
        int maxRounds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the coordinator for a Windows desktop AI product.");
        builder.AppendLine("Your job is to choose a compact team of specialists for requirement discussion and solution design.");
        builder.AppendLine("Return JSON only. Do not use Markdown fences.");
        builder.AppendLine("Schema:");
        builder.AppendLine("{\"documentTitle\":\"string\",\"agents\":[{\"name\":\"string\",\"role\":\"string\",\"mission\":\"string\"}]}");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Team size limit is 5 total members including the coordinator.");
        builder.AppendLine($"- The specialists will discuss the task for at most {maxRounds} rounds, so prefer a small team that can build on each other's feedback.");
        builder.AppendLine("- Prefer specialists like PM, architect, DBA, backend, security, frontend only when relevant.");
        builder.AppendLine("- Prefer reusing the existing team when it already covers the task.");
        builder.AppendLine("- Each mission should be one concise sentence focused on analysis, not coding.");
        if (workspaceRoot is null)
        {
            builder.AppendLine("- No workspace is selected, so plan for discussion and documentation only.");
        }
        else
        {
            builder.AppendLine($"- The trusted workspace root is '{workspaceRoot.RootPath}'. Specialists may inspect it read-only.");
        }

        if (existingTeamAgents.Count > 0)
        {
            builder.AppendLine("- Existing team members are already available; only add new specialties when there is a gap.");
        }

        return builder.ToString();
    }


    private static string BuildCoordinatorSummaryInstructions(
        WorkspaceRoot? workspaceRoot,
        TeamOutputMode outputMode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the main coordinator in a team discussion workflow.");
        builder.AppendLine("Synthesize and de-duplicate specialist feedback. Preserve conflicts as explicit decisions or open questions.");
        builder.AppendLine("Mention assumptions and unknowns clearly.");
        switch (outputMode)
        {
            case TeamOutputMode.ReplyOnly:
                builder.AppendLine("Write the final answer in Markdown for chat.");
                builder.AppendLine("Be direct and concise. Use sections only when they improve clarity.");
                builder.AppendLine("Do not force the response into a standalone document unless the user explicitly asked for one.");
                break;
            case TeamOutputMode.AlwaysDocument:
                builder.AppendLine("Write the final answer in Markdown as a design document suitable for saving to a .md file.");
                builder.AppendLine("Structure the document with: title, background, requirements, proposed design, data model, key flows, risks, open questions, and implementation guidance.");
                if (workspaceRoot is null)
                {
                    builder.AppendLine("Make it explicit that no workspace was selected, so the document remains in chat unless the user later selects one.");
                }
                break;
            default:
                builder.AppendLine("Write the final answer in Markdown for chat first.");
                builder.AppendLine("Use a short summary structure that can stand alone in chat without feeling like a forced file export.");
                builder.AppendLine("If the user clearly asked for a formal plan or specification, you may make the answer more document-like.");
                break;
        }

        return builder.ToString();
    }


    private static string BuildDocumentDecisionInstructions()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Decide whether the final team answer should also be exported as a Markdown document.");
        builder.AppendLine("Return JSON only with this schema:");
        builder.AppendLine("{\"shouldExportDocument\":true|false}");
        builder.AppendLine("Choose true only when a saved document would materially help, such as for implementation plans, design specs, requirements docs, or persistent reports.");
        builder.AppendLine("Choose false for normal Q&A, quick explanations, or ad-hoc opinions that are sufficient in chat.");
        return builder.ToString();
    }


    private static string BuildRoundContinuationDecisionInstructions(int currentRound, int maxRounds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Decide whether the specialist team needs another discussion round before the final coordinator summary.");
        builder.AppendLine("Return JSON only with this schema:");
        builder.AppendLine("{\"continueDiscussion\":true|false}");
        builder.AppendLine($"The team has completed round {currentRound} and can discuss at most {maxRounds} rounds in total.");
        builder.AppendLine("Choose true only when another round is likely to materially improve the answer by resolving important disagreements, filling major gaps, or challenging weak assumptions.");
        builder.AppendLine("Choose false when the current discussion already has enough coverage or when another round would mostly repeat the same points.");
        return builder.ToString();
    }


    private static string BuildWorkerInstructions(TeamBlueprintAgent agent, WorkspaceRoot? workspaceRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You are {agent.Name}, acting as {agent.Role}.");
        builder.AppendLine(agent.Mission);
        builder.AppendLine("Respond in Markdown.");
        builder.AppendLine("Focus on your specialty, surface assumptions, identify risks, and suggest concrete design choices.");
        builder.AppendLine("The coordinator may call you for multiple discussion rounds, so you should refine your position when new specialist feedback appears.");
        builder.AppendLine("Do not write the final consolidated answer. Do not claim tools were used unless tool results were actually returned.");
        if (workspaceRoot is null)
        {
            builder.AppendLine("No workspace is selected, so work from the task description and prior discussion only.");
        }
        else
        {
            builder.AppendLine($"The trusted workspace root is '{workspaceRoot.RootPath}'. You may inspect it read-only via list/search/read tools when helpful.");
        }

        return builder.ToString();
    }


    private static string BuildWorkerRoundInstructions(int roundNumber, int maxRounds)
    {
        if (roundNumber <= 1)
        {
            return $"This is discussion round 1 of up to {maxRounds}. Provide your initial analysis, preferred approach, major risks, and concrete recommendations from your specialty.";
        }

        return $"This is discussion round {roundNumber} of up to {maxRounds}. Read the shared specialist discussion carefully, react to the other agents by name when helpful, correct weak assumptions, resolve conflicts where possible, and add only the net-new insight or revisions that matter.";
    }

}
