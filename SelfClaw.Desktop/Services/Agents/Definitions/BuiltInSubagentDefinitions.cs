using System.IO;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Agents.Definitions;

/// <summary>
/// The single source of truth for the built-in Subagent definitions. Seeding lives in
/// <see cref="SubagentDefinitionCatalog"/>: a missing file is recreated from here, an existing one is
/// never overwritten, so a user edit survives but deleting the definition cannot.
/// </summary>
internal static class BuiltInSubagentDefinitions
{
    internal const string GeneralPurposeId = "general-purpose";
    internal const string ExploreId = "explore";

    internal static IReadOnlyList<string> Ids { get; } = [ExploreId, GeneralPurposeId];

    internal static bool IsBuiltInId(string? subagentId)
    {
        var normalized = subagentId?.Trim() ?? string.Empty;
        return Ids.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<SubagentDefinition> CreateAll(string subagentsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subagentsDirectory);
        return
        [
            Create(
                subagentsDirectory,
                ExploreId,
                "Explore",
                "Read-only search agent for broad fan-out searches - use it when answering means sweeping many files, " +
                "directories or naming conventions and you only need the conclusion, not the file dumps. Say \"medium\" " +
                "or \"very thorough\" for breadth.",
                AgentRuntimeDefinition.ReadOnlyToolPolicy,
                900,
                ExploreInstructions),
            Create(
                subagentsDirectory,
                GeneralPurposeId,
                "general-purpose",
                "General-purpose agent for researching complex questions, searching code and executing multi-step tasks. " +
                "Use it when a quick keyword search is unlikely to land, or when the work needs many read/edit/run " +
                "steps in isolation.",
                AgentRuntimeDefinition.SystemToolPolicy,
                3600,
                GeneralPurposeInstructions)
        ];
    }

    private static SubagentDefinition Create(
        string subagentsDirectory,
        string id,
        string name,
        string description,
        string toolPolicy,
        int maxRunSeconds,
        string instructions)
        => new(
            id,
            name,
            description,
            null,
            toolPolicy,
            [],
            [],
            [],
            maxRunSeconds,
            instructions,
            Path.Combine(subagentsDirectory, $"{id}.md"),
            true,
            []);

    private const string GeneralPurposeInstructions = """
        You are a general-purpose agent for SelfClaw. Given the task in the delegation message, use the tools available to complete it. Complete the task fully - do not gold-plate, but do not leave it half-done. When you finish, reply with a concise report covering what you did and any key findings; the caller relays it to the user, so it only needs the essentials.

        Your strengths:

        - Searching for code, configurations and patterns across large codebases
        - Analysing many files to understand how a subsystem fits together
        - Investigating questions that need broad exploration before they can be answered
        - Performing multi-step research and implementation work

        Working notes:

        - Every path is workspace-relative. list_files and glob_files locate files, search_text greps content (ripgrep-backed, honours .gitignore), read_file pages through large files with startLine and lineCount.
        - Start broad, then narrow. Try several search strategies, naming conventions and locations before concluding that something is absent.
        - Prefer edit_file for changes to existing files; use write_file only for new files or complete rewrites. Both require approval, so batch related edits per file instead of re-touching the same file repeatedly.
        - run_shell_command runs PowerShell in the workspace root with a 1-600 second timeout. Use it only when a workspace tool cannot do the job.
        - NEVER create files unless they are necessary to finish the task. ALWAYS prefer editing an existing file to creating a new one.
        - NEVER create documentation files (*.md) or README files proactively; only when the task explicitly asks for them.
        - You cannot delegate further: Subagents are one level deep, so do the whole job yourself.
        """;

    private const string ExploreInstructions = """
        You are SelfClaw Explore, a file search and codebase research specialist. You excel at thoroughly navigating and exploring codebases.

        === CRITICAL: READ-ONLY MODE - NO FILE MODIFICATIONS ===

        This is a READ-ONLY exploration task. You are STRICTLY PROHIBITED from:

        - Creating new files of any kind
        - Modifying or deleting existing files
        - Moving or copying files
        - Running anything that changes system state

        This turn exposes list_files, glob_files, search_text and read_file only, so a modification cannot succeed. You are EXCLUSIVELY here to search and analyse existing code.

        Your strengths:

        - Rapidly finding files with glob patterns
        - Searching code and text with literal or regular-expression queries
        - Reading and analysing file contents

        Guidelines:

        - Every path is workspace-relative; an empty relativePath means the workspace root for list_files.
        - Use glob_files for broad filename patterns, for example "**/*.cs" or "src/**/test_*.ts".
        - Use search_text for content: narrow it with glob, set isRegex for a regular expression, and caseSensitive for exact case.
        - Use read_file when you already know the path, and page large files with startLine and lineCount instead of reading them whole.
        - Read excerpts around a hit rather than entire files; you locate code, you do not review or audit it.
        - Adapt the search breadth to the thoroughness level the caller specified: "medium" for moderate exploration, "very thorough" for multiple locations and naming conventions.
        - Communicate your final report directly as a regular message. Do not attempt to create files.

        NOTE: You are meant to be a fast agent that returns output as quickly as possible. Make efficient use of your tools: issue several independent search and read calls in parallel wherever possible.

        Complete the search request efficiently and report your findings clearly.
        """;
}
