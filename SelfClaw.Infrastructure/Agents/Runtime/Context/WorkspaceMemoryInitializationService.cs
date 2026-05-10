using System.Text;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Execution;

namespace SelfClaw.Infrastructure.Agents.Runtime.Context;

internal sealed class WorkspaceMemoryInitializationService : IWorkspaceMemoryInitializationService
{
    private const int MaxEntries = 320;
    private const int MaxReadmeCharacters = 12_000;

    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly IAgentExecutionService _agentExecutionService;

    public WorkspaceMemoryInitializationService(
        IWorkspaceToolService workspaceToolService,
        IAgentExecutionService agentExecutionService)
    {
        _workspaceToolService = workspaceToolService;
        _agentExecutionService = agentExecutionService;
    }

    public bool AgentsFileExists(WorkspaceRoot workspaceRoot)
        => File.Exists(Path.Combine(workspaceRoot.RootPath, "AGENTS.md"));

    public async Task<WorkspaceFileWriteResult> InitializeAsync(
        WorkspaceRoot workspaceRoot,
        ProviderProfile profile,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await BuildWorkspaceSnapshotAsync(workspaceRoot, cancellationToken);
        var generated = await GenerateAgentsMarkdownAsync(profile, apiKey, snapshot, cancellationToken);
        return await _workspaceToolService.WriteFileAsync(
            workspaceRoot.RootPath,
            "AGENTS.md",
            generated,
            cancellationToken);
    }

    private async Task<string> BuildWorkspaceSnapshotAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Workspace name: {workspaceRoot.Name}");
        builder.AppendLine($"Workspace root: {workspaceRoot.RootPath}");
        builder.AppendLine();
        builder.AppendLine("File tree sample:");

        var entries = await CollectEntriesAsync(workspaceRoot.RootPath, string.Empty, 0, cancellationToken);
        foreach (var entry in entries.Take(MaxEntries))
        {
            builder.AppendLine(entry);
        }

        var readme = await TryReadFirstExistingFileAsync(
            workspaceRoot.RootPath,
            ["README.md", "readme.md", "Readme.md"],
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(readme))
        {
            builder.AppendLine();
            builder.AppendLine("README excerpt:");
            builder.AppendLine(TrimForPrompt(readme, MaxReadmeCharacters));
        }

        return builder.ToString();
    }

    private async Task<IReadOnlyList<string>> CollectEntriesAsync(
        string workspaceRootPath,
        string relativePath,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 3)
        {
            return [];
        }

        var results = new List<string>();
        IReadOnlyList<WorkspaceFileEntry> entries;
        try
        {
            entries = await _workspaceToolService.ListFilesAsync(workspaceRootPath, relativePath, cancellationToken);
        }
        catch
        {
            return results;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipEntry(entry.RelativePath))
            {
                continue;
            }

            var prefix = new string(' ', depth * 2);
            results.Add($"{prefix}{(entry.IsDirectory ? "[dir]" : "[file]")} {NormalizePath(entry.RelativePath)}");
            if (entry.IsDirectory && results.Count < MaxEntries)
            {
                results.AddRange(await CollectEntriesAsync(workspaceRootPath, entry.RelativePath, depth + 1, cancellationToken));
            }

            if (results.Count >= MaxEntries)
            {
                break;
            }
        }

        return results;
    }

    private async Task<string?> TryReadFirstExistingFileAsync(
        string workspaceRootPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in relativePaths)
        {
            try
            {
                var content = await _workspaceToolService.ReadFileAsync(workspaceRootPath, relativePath, cancellationToken);
                return content.Content;
            }
            catch
            {
                // Best-effort workspace summary.
            }
        }

        return null;
    }

    private async Task<string> GenerateAgentsMarkdownAsync(
        ProviderProfile profile,
        string apiKey,
        string snapshot,
        CancellationToken cancellationToken)
    {
        var request = new AgentExecutionRequest(
            profile,
            apiKey,
            "ProjectInitializer",
            "Generates repository memory instructions for SelfClaw.",
            BuildInstructions(),
            [new ChatMessage(ChatRole.User, snapshot)],
            [],
            ContextProviders: null,
            EnableReasoning: false);

        var result = await _agentExecutionService.RunAsync(request, onTextDelta: null, cancellationToken);
        var markdown = StripMarkdownFence(result.FinalMarkdown).Trim();
        return string.IsNullOrWhiteSpace(markdown)
            ? BuildFallbackAgentsMarkdown(snapshot)
            : markdown + Environment.NewLine;
    }

    private static string BuildInstructions()
        => """
Create an AGENTS.md memory file for this repository.

Requirements:
- Use concise Markdown.
- Include project overview, structure, build/test commands, architecture notes, conventions, and important caveats.
- Write instructions for future coding agents working in this workspace.
- Do not invent facts not supported by the provided snapshot.
- If a detail is unknown, omit it rather than guessing.
- Return only the AGENTS.md content, with no surrounding commentary or code fence.
""";

    private static string BuildFallbackAgentsMarkdown(string snapshot)
        => $"""
# Project Context

This file was generated from a workspace snapshot. Review and refine it with project-specific conventions.

## Workspace Snapshot

```text
{snapshot.Trim()}
```
""";

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineEnd)
        {
            return trimmed;
        }

        return trimmed[(firstLineEnd + 1)..lastFence].Trim();
    }

    private static string TrimForPrompt(string value, int maxCharacters)
        => value.Length <= maxCharacters
            ? value
            : value[..maxCharacters] + "\n[truncated]";

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private static bool ShouldSkipEntry(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is
                ".git" or ".vs" or ".idea" or ".vscode" or "bin" or "obj" or "node_modules" or "dist" or "build" or ".cache");
    }
}
