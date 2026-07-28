using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Extensions;

/// <summary>
/// Single answer to "is this installed package usable". The settings page, the composer's Skill list
/// and the Direct capability resolver must agree: a Skill whose SKILL.md is gone has to read as broken
/// everywhere, otherwise the picker offers a Skill that fails the turn.
/// </summary>
internal static class ExtensionInstallation
{
    public const string SkillManifestName = "SKILL.md";
    public const string PluginManifestName = "plugin.json";

    public static bool IsIntact(ExtensionPackageRecord package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Directory.Exists(package.InstallPath) &&
            (package.Kind != ExtensionKind.Skill || File.Exists(SkillManifestPath(package)));
    }

    public static string SkillManifestPath(ExtensionPackageRecord package)
        => Path.Combine(package.InstallPath, SkillManifestName);

    public static string PluginManifestPath(ExtensionPackageRecord package)
        => Path.Combine(package.InstallPath, PluginManifestName);
}
