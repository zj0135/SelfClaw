#pragma warning disable MAAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class FileSystemAgentContextProviderFactory : IAgentContextProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly StoragePaths _storagePaths;
    private readonly IReadOnlyList<string> _assetsRootPaths;

    public FileSystemAgentContextProviderFactory(ILoggerFactory loggerFactory, StoragePaths storagePaths)
        : this(loggerFactory,storagePaths, DiscoverDefaultAssetsRootPaths(AppContext.BaseDirectory))
    {
    }

    internal FileSystemAgentContextProviderFactory(ILoggerFactory loggerFactory, StoragePaths storagePaths, params string[] assetsRootPaths)
        : this(loggerFactory,storagePaths, (IEnumerable<string>)assetsRootPaths)
    {
    }

    internal FileSystemAgentContextProviderFactory(ILoggerFactory loggerFactory, StoragePaths storagePaths, IEnumerable<string> assetsRootPaths)
    {
        _loggerFactory = loggerFactory;
        _storagePaths = storagePaths;

        _assetsRootPaths = assetsRootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<AIContextProvider> CreateProviders()
    {
        var skillRoots = DiscoverSkillRoots();
        if (skillRoots.Count == 0)
        {
            return [];
        }

        var fileOptions = new AgentFileSkillsSourceOptions
        {
            // Skill scripts need an explicit execution runner. Until SelfClaw wires one up with
            // approval semantics, keep discovery limited to SKILL.md and read-only resources.
            AllowedScriptExtensions = []
        };
        var providerOptions = new AgentSkillsProviderOptions
        {
            DisableCaching = true,
        };

        return
        [
            new AgentSkillsProvider(
                skillRoots,
                scriptRunner: null,
                fileOptions,
                providerOptions,
                _loggerFactory)
        ];
    }

    internal IReadOnlyList<string> DiscoverSkillRoots()
    {
        var roots = new List<string>();

        var storagePath = Path.Combine(_storagePaths.AppDataDirectory, "skills");
        if (Directory.Exists(storagePath))
        {
            var hasSkillManifest = Directory
                .EnumerateFiles(storagePath, "SKILL.md", SearchOption.AllDirectories)
                .Any();

            if (hasSkillManifest)
                roots.Add(Path.GetFullPath(storagePath));
            
        }

        foreach (var assetsRootPath in _assetsRootPaths)
        {
            var skillsRootPath = Path.Combine(assetsRootPath, "skills");
            if (!Directory.Exists(skillsRootPath))
            {
                continue;
            }

            var hasSkillManifest = Directory
                .EnumerateFiles(skillsRootPath, "SKILL.md", SearchOption.AllDirectories)
                .Any();
            if (!hasSkillManifest)
            {
                continue;
            }

            roots.Add(Path.GetFullPath(skillsRootPath));
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> DiscoverDefaultAssetsRootPaths(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return [];
        }

        var ancestors = EnumerateAncestorDirectories(baseDirectory).ToArray();
        var candidatePaths = new List<string>(ancestors.Length * 2);

        // Prefer the source-tree Desktop assets when debugging from bin/obj paths, so newly added
        // skills under the project are visible even before a fresh asset copy occurs.
        foreach (var ancestor in ancestors)
        {
            candidatePaths.Add(Path.Combine(ancestor, "SelfClaw.Desktop", "Assets"));
        }

        foreach (var ancestor in ancestors)
        {
            candidatePaths.Add(Path.Combine(ancestor, "Assets"));
        }

        return candidatePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string startPath)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(startPath));
             current is not null;
             current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}

#pragma warning restore MAAI001
