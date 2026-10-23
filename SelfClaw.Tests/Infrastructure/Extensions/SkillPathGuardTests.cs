using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class SkillPathGuardTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveWithinRoot_rejects_absolute_paths_and_unsafe_segments()
    {
        var root = Path.Combine(_rootPath, "skill");
        Directory.CreateDirectory(root);

        foreach (var relativePath in new[] { "C:/Windows/win.ini", "../outside.md", "./inside.md", "a//b.md" })
        {
            var action = () => SkillPathGuard.ResolveWithinRoot(root, relativePath);
            action.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void EnsureNoReparsePoints_accepts_a_file_under_a_linked_root()
    {
        // The reported shape: %UserProfile%\.agents\skills\<id> is a junction into a working copy. The root is
        // picked by the user, so its own reparse point must not be treated as package-controlled reach.
        var target = Path.Combine(_rootPath, "checkout", "teach");
        var root = Path.Combine(_rootPath, "skills", "teach");
        WriteFile(target, "SKILL.md", "body");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        if (!DirectoryLinkTestHelper.TryCreate(root, target))
        {
            return;
        }

        var action = () => SkillPathGuard.EnsureNoReparsePoints(root, Path.Combine(root, "SKILL.md"));

        action.Should().NotThrow();
        File.ReadAllText(Path.Combine(root, "SKILL.md")).Should().Be("body");
    }

    [Fact]
    public void EnsureNoReparsePoints_rejects_a_link_below_the_root()
    {
        var root = Path.Combine(_rootPath, "skill");
        var outside = Path.Combine(_rootPath, "outside");
        Directory.CreateDirectory(root);
        WriteFile(outside, "secret.md", "secret");
        if (!DirectoryLinkTestHelper.TryCreate(Path.Combine(root, "references"), outside))
        {
            return;
        }

        var action = () => SkillPathGuard.EnsureNoReparsePoints(
            root,
            Path.Combine(root, "references", "secret.md"));

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EnsureNoReparsePoints_rejects_a_path_that_is_not_under_the_root()
    {
        var root = Path.Combine(_rootPath, "skill");
        WriteFile(root, "guide.md", "guide");
        WriteFile(Path.Combine(_rootPath, "sibling"), "guide.md", "guide");

        SkillPathGuard.EnsureNoReparsePoints(root, Path.Combine(root, "guide.md"));

        var action = () => SkillPathGuard.EnsureNoReparsePoints(
            root,
            Path.Combine(_rootPath, "sibling", "guide.md"));

        action.Should().Throw<InvalidDataException>();
    }

    public void Dispose() => DirectoryLinkTestHelper.DeleteTree(_rootPath);

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
