using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Skills;

internal sealed class SkillRuntimeToolset
{
    internal const string ActivateSkillToolName = "activate_skill";
    internal const string ReadSkillResourceToolName = "read_skill_resource";
    private const int MaximumActivatedSkills = 5;
    private const int DefaultLineCount = 200;
    private const int MaximumLineCount = 500;
    private const long MaximumResourceBytes = 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".jsonl", ".yaml", ".yml", ".toml", ".ini", ".xml",
        ".csv", ".tsv", ".cs", ".js", ".jsx", ".ts", ".tsx", ".vue", ".html", ".css",
        ".scss", ".py", ".ps1", ".sh", ".java", ".go", ".rs"
    };

    public IReadOnlyList<AITool> CreateTools(
        IReadOnlyList<ResolvedSkill> skills,
        IReadOnlyList<string> explicitlyActivatedSkillIds)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(explicitlyActivatedSkillIds);
        if (skills.Count == 0)
        {
            return [];
        }

        var bound = new BoundSkillTools(skills, explicitlyActivatedSkillIds);
        return
        [
            AIFunctionFactory.Create(
                bound.ActivateSkillAsync,
                ActivateSkillToolName,
                "Load the complete instructions for an available Skill for this turn."),
            AIFunctionFactory.Create(
                bound.ReadSkillResourceAsync,
                ReadSkillResourceToolName,
                "Read a paged UTF-8 text resource from a Skill that is activated for this turn.")
        ];
    }

    private sealed class BoundSkillTools
    {
        private readonly IReadOnlyDictionary<string, ResolvedSkill> _skills;
        private readonly HashSet<string> _activatedSkillIds;
        private readonly object _gate = new();

        public BoundSkillTools(
            IReadOnlyList<ResolvedSkill> skills,
            IReadOnlyList<string> explicitlyActivatedSkillIds)
        {
            _skills = skills.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
            _activatedSkillIds = explicitlyActivatedSkillIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public Task<string> ActivateSkillAsync(
            [Description("The exact Skill id from the available Skill catalog.")] string skillId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_skills.TryGetValue(skillId, out var skill))
            {
                return Task.FromResult($"Skill '{skillId}' is not available to the current Agent.");
            }

            lock (_gate)
            {
                if (_activatedSkillIds.Contains(skill.Id))
                {
                    return Task.FromResult($"Skill '{skill.Id}' is already activated for this turn.");
                }

                if (_activatedSkillIds.Count >= MaximumActivatedSkills)
                {
                    return Task.FromResult("This turn has reached the limit of 5 activated Skills.");
                }

                _activatedSkillIds.Add(skill.Id);
            }

            return Task.FromResult(skill.Content);
        }

        public async Task<string> ReadSkillResourceAsync(
            [Description("The exact id of an activated Skill.")] string skillId,
            [Description("Skill-root-relative text file path.")] string relativePath,
            [Description("One-based first line to return. Defaults to 1.")] int? startLine = null,
            [Description("Number of lines to return. Defaults to 200 and cannot exceed 500.")] int? lineCount = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_skills.TryGetValue(skillId, out var skill))
            {
                return $"Skill '{skillId}' is not available to the current Agent.";
            }

            lock (_gate)
            {
                if (!_activatedSkillIds.Contains(skill.Id))
                {
                    return $"Skill '{skill.Id}' is not activated for this turn.";
                }
            }

            var filePath = ResolveResourcePath(skill.InstallPath, relativePath);
            if (!File.Exists(filePath))
            {
                return $"Skill resource '{relativePath}' was not found.";
            }

            RejectReparsePoints(skill.InstallPath, filePath);
            if (!TextExtensions.Contains(Path.GetExtension(filePath)))
            {
                return $"Skill resource '{relativePath}' is not an allowed text file type.";
            }

            var file = new FileInfo(filePath);
            if (file.Length > MaximumResourceBytes)
            {
                return $"Skill resource '{relativePath}' exceeds the {MaximumResourceBytes} byte limit.";
            }

            var firstLine = startLine ?? 1;
            var count = lineCount ?? DefaultLineCount;
            if (firstLine <= 0 || count <= 0 || count > MaximumLineCount)
            {
                return "startLine must be positive and lineCount must be between 1 and 500.";
            }

            string content;
            await using (var stream = new FileStream(
                             filePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (firstLine > lines.Length)
            {
                return $"startLine {firstLine} exceeds the resource's {lines.Length} lines.";
            }

            var selected = lines.Skip(firstLine - 1).Take(count).ToArray();
            var lastLine = firstLine + selected.Length - 1;
            return $"{relativePath} (lines {firstLine}-{lastLine} of {lines.Length})\n" + string.Join("\n", selected);
        }

        private static string ResolveResourcePath(string skillRoot, string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            {
                throw new InvalidDataException("Skill resource path must be relative.");
            }

            var segments = relativePath.Replace('\\', '/').Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            {
                throw new InvalidDataException("Skill resource path contains an unsafe segment.");
            }

            var root = Path.GetFullPath(skillRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Skill resource path escapes the Skill directory.");
            }

            return candidate;
        }

        private static void RejectReparsePoints(string skillRoot, string filePath)
        {
            var root = Path.GetFullPath(skillRoot);
            var current = Path.GetFullPath(filePath);
            while (current.Length >= root.Length)
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Skill resources cannot use symbolic links or reparse points.");
                }

                if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidDataException("Skill resource path escapes the Skill directory.");
            }
        }
    }
}
