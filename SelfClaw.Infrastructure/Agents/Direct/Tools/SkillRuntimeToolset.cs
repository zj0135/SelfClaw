using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

internal sealed class SkillRuntimeToolset
{
    internal const string ActivateSkillToolName = "activate_skill";
    internal const string ReadSkillResourceToolName = "read_skill_resource";
    internal const string RunSkillScriptToolName = "run_skill_script";
    internal const string ListSkillResourcesToolName = "list_skill_resources";
    private const int MaximumActivatedSkills = 5;
    private const int DefaultLineCount = 200;
    private const int MaximumLineCount = 500;
    private const int DefaultListPageSize = 50;
    private const int MaximumListPageSize = 200;
    private const long MaximumResourceBytes = 1024 * 1024;

    private readonly SkillScriptRunner _scriptRunner;

    public SkillRuntimeToolset(SkillScriptRunner scriptRunner)
    {
        ArgumentNullException.ThrowIfNull(scriptRunner);
        _scriptRunner = scriptRunner;
    }

    public IReadOnlyList<DirectToolBinding> CreateTools(
        IReadOnlyList<ResolvedSkill> skills,
        IReadOnlyList<string> explicitlyActivatedSkillIds)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(explicitlyActivatedSkillIds);
        if (skills.Count == 0)
        {
            return [];
        }

        var bound = new BoundSkillTools(skills, explicitlyActivatedSkillIds, _scriptRunner);
        return
        [
            DirectToolBindings.Create(
                bound.ActivateSkillAsync,
                ActivateSkillToolName,
                "Load the complete instructions for an available Skill for this turn.",
                ToolCallKind.Read, ToolSourceKind.Skill, "Activate Skill"),
            DirectToolBindings.Create(
                bound.ReadSkillResourceAsync,
                ReadSkillResourceToolName,
                "Read a paged UTF-8 resource from a Skill that is activated for this turn. A script can be "
                    + "inspected this way, but must be executed with " + RunSkillScriptToolName + ".",
                ToolCallKind.Read, ToolSourceKind.Skill, "Read Skill resource"),
            DirectToolBindings.Create(
                bound.RunSkillScriptAsync,
                RunSkillScriptToolName,
                "Execute a script that an activated Skill owns. The script runs with the Skill directory as "
                    + "its working directory and inherits only a minimal environment.",
                ToolCallKind.Run, ToolSourceKind.Skill, "Run skill script",
                requiresApproval: true),
            DirectToolBindings.Create(
                bound.ListSkillResourcesAsync,
                ListSkillResourcesToolName,
                "Page through every file an activated Skill owns, labelled as a readable resource or an "
                    + "executable script. Use it when the activation summary was truncated.",
                ToolCallKind.List, ToolSourceKind.Skill, "List Skill resources")
        ];
    }

    private sealed class BoundSkillTools
    {
        private readonly IReadOnlyDictionary<string, ResolvedSkill> _skills;
        private readonly HashSet<string> _activatedSkillIds;
        private readonly SkillScriptRunner _scriptRunner;

        public BoundSkillTools(
            IReadOnlyList<ResolvedSkill> skills,
            IReadOnlyList<string> explicitlyActivatedSkillIds,
            SkillScriptRunner scriptRunner)
        {
            _skills = skills.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
            _activatedSkillIds = explicitlyActivatedSkillIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _scriptRunner = scriptRunner;
        }

        public Task<DirectToolResult> ActivateSkillAsync(
            [Description("The exact Skill id from the available Skill catalog.")] string skillId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolve(skillId, out var skill, out var failure))
            {
                return Task.FromResult(failure);
            }

            // The loop executes a turn's tools one at a time on this per-turn instance, so the activated set
            // needs no lock: activation is only ever written here and read by the other three tools.
            if (_activatedSkillIds.Contains(skill.Id))
            {
                var alreadyActive = $"Skill '{skill.Id}' is already activated for this turn.";
                return Task.FromResult(Loaded(alreadyActive, alreadyActive));
            }

            if (_activatedSkillIds.Count >= MaximumActivatedSkills)
            {
                return Task.FromResult(Failed("This turn has reached the limit of 5 activated Skills."));
            }

            _activatedSkillIds.Add(skill.Id);
            return Task.FromResult(Loaded(
                $"{skill.Content}\n\n{SkillResourceFormatter.Manifest(skill.Id, skill.Resources)}",
                $"Activated Skill '{skill.Id}'."));
        }

        public async Task<DirectToolResult> ReadSkillResourceAsync(
            [Description("The exact id of an activated Skill.")] string skillId,
            [Description("Skill-root-relative text file path.")] string relativePath,
            [Description("One-based first line to return. Defaults to 1.")] int? startLine = null,
            [Description("Number of lines to return. Defaults to 200 and cannot exceed 500.")] int? lineCount = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveActivated(skillId, out var skill, out var failure))
            {
                return failure;
            }

            var filePath = SkillPathGuard.ResolveWithinRoot(skill.InstallPath, relativePath);
            if (!File.Exists(filePath))
            {
                return Failed($"Skill resource '{relativePath}' was not found.");
            }

            SkillPathGuard.EnsureNoReparsePoints(skill.InstallPath, filePath);
            var kind = SkillResourceIndexer.Classify(Path.GetExtension(filePath));
            if (kind is null)
            {
                return Failed($"Skill resource '{relativePath}' is not an allowed text file type.");
            }

            var file = new FileInfo(filePath);
            if (file.Length > MaximumResourceBytes)
            {
                return Failed($"Skill resource '{relativePath}' exceeds the {MaximumResourceBytes} byte limit.");
            }

            var firstLine = startLine ?? 1;
            var count = lineCount ?? DefaultLineCount;
            if (firstLine <= 0 || count <= 0 || count > MaximumLineCount)
            {
                return Failed("startLine must be positive and lineCount must be between 1 and 500.");
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
                return Failed($"startLine {firstLine} exceeds the resource's {lines.Length} lines.");
            }

            var selected = lines.Skip(firstLine - 1).Take(count).ToArray();
            var lastLine = firstLine + selected.Length - 1;
            var range = $"lines {firstLine}-{lastLine} of {lines.Length}";
            // A script stays readable so the model can confirm the parameters it takes, but the result has to
            // say what it is: reading a script and running it are different acts, and conflating them is
            // what produced the reimplemented-in-shell behaviour this toolset exists to remove.
            var header = kind == SkillResourceKind.Script
                ? $"{relativePath} [script] ({range})\n"
                    + $"Executable script: run it with {RunSkillScriptToolName} instead of transcribing or reimplementing its body.\n"
                : $"{relativePath} ({range})\n";
            return Loaded(header + string.Join("\n", selected), $"Read {relativePath} ({range}).");
        }

        public async Task<DirectToolResult> RunSkillScriptAsync(
            [Description("The exact id of an activated Skill.")] string skillId,
            [Description("Skill-root-relative path to the script.")] string relativePath,
            [Description("Optional arguments passed to the script as separate tokens.")] string[]? arguments = null,
            [Description("Maximum execution time in seconds, from 1 to 600. Defaults to 60.")] int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveActivated(skillId, out var skill, out var failure))
            {
                return failure;
            }

            var result = await _scriptRunner.RunAsync(
                    skill.InstallPath,
                    relativePath,
                    arguments,
                    timeoutSeconds ?? SkillScriptRunner.DefaultTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DirectToolResult(
                result.Executed && result.ExitCode == 0 ? ToolCallStatus.Completed : ToolCallStatus.Failed,
                result.Message,
                JsonSerializer.SerializeToElement(result),
                BuildScriptDetail(result));
        }

        public Task<DirectToolResult> ListSkillResourcesAsync(
            [Description("The exact id of an activated Skill.")] string skillId,
            [Description("Optional Skill-root-relative directory to scope the listing to.")] string? relativeDirectory = null,
            [Description("Zero-based index of the first entry to return. Defaults to 0.")] int? startIndex = null,
            [Description("Number of entries to return, from 1 to 200. Defaults to 50.")] int? count = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveActivated(skillId, out var skill, out var failure))
            {
                return Task.FromResult(failure);
            }

            var scoped = ScopeResources(skill, relativeDirectory);
            var from = Math.Max(startIndex ?? 0, 0);
            var take = Math.Clamp(count ?? DefaultListPageSize, 1, MaximumListPageSize);
            var page = scoped.Skip(from).Take(take).ToArray();
            return Task.FromResult(new DirectToolResult(
                ToolCallStatus.Completed,
                $"Listed {page.Length} of {scoped.Count} Skill resources.",
                JsonSerializer.SerializeToElement(page),
                SkillResourceFormatter.Listing(skill.Id, page, from, scoped.Count, skill.Resources.Truncated)));
        }

        private bool TryResolve(string skillId, [NotNullWhen(true)] out ResolvedSkill? skill,
            [NotNullWhen(false)] out DirectToolResult? failure)
        {
            if (_skills.TryGetValue(skillId, out var resolved))
            {
                skill = resolved;
                failure = null;
                return true;
            }

            skill = null;
            failure = Failed($"Skill '{skillId}' is not available to the current Agent.");
            return false;
        }

        /// <summary>
        /// Every tool except activate_skill only operates on a Skill the turn has activated; both rejections
        /// reach the model as a Failed result so it can activate the Skill and retry.
        /// </summary>
        private bool TryResolveActivated(string skillId, [NotNullWhen(true)] out ResolvedSkill? skill,
            [NotNullWhen(false)] out DirectToolResult? failure)
        {
            if (!TryResolve(skillId, out skill, out failure))
            {
                return false;
            }

            if (_activatedSkillIds.Contains(skill.Id))
            {
                return true;
            }

            failure = Failed($"Skill '{skill.Id}' is not activated for this turn.");
            return false;
        }

        /// <summary>
        /// Resolving through the guard rejects absolute paths and <c>..</c> before the value is used as a
        /// path-shaped filter, so a traversal attempt cannot widen the listing.
        /// </summary>
        private static IReadOnlyList<SkillResourceEntry> ScopeResources(ResolvedSkill skill, string? relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory))
            {
                return skill.Resources.Entries;
            }

            var directory = SkillPathGuard.ResolveWithinRoot(skill.InstallPath, relativeDirectory);
            var prefix = Path.GetRelativePath(skill.InstallPath, directory).Replace('\\', '/').TrimEnd('/');
            return skill.Resources.Entries
                .Where(entry => entry.RelativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static string BuildScriptDetail(SkillScriptResult result)
        {
            var detail = new StringBuilder(result.Message);
            if (result.StandardOutput.Length > 0)
            {
                detail.Append('\n').Append(result.StandardOutput);
            }

            if (result.StandardError.Length > 0)
            {
                detail.Append("\nstderr:\n").Append(result.StandardError);
            }

            return detail.ToString();
        }

        /// <summary>
        /// One shape for a model-correctable rejection: the message is the summary, the model content and the
        /// detail, because there is nothing else to say about a call that never ran.
        /// </summary>
        private static DirectToolResult Failed(string text)
            => new(ToolCallStatus.Failed, text, JsonSerializer.SerializeToElement(text), text);

        /// <summary>
        /// A successful result says in one line what the call did; the loaded content stays in the detail.
        /// </summary>
        private static DirectToolResult Loaded(string text, string summary)
            => new(ToolCallStatus.Completed, summary, JsonSerializer.SerializeToElement(text), text);
    }
}
