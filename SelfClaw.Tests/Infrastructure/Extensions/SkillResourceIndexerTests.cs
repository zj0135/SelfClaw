using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class SkillResourceIndexerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".md", "Resource")]
    [InlineData(".JSON", "Resource")]
    [InlineData(".ps1", "Script")]
    [InlineData(".PY", "Script")]
    [InlineData(".cmd", "Script")]
    public void Classify_uses_separate_tables_for_resources_and_scripts(string extension, string expected)
        => SkillResourceIndexer.Classify(extension)?.ToString().Should().Be(expected);

    [Theory]
    [InlineData(".png")]
    [InlineData(".exe")]
    [InlineData(".dll")]
    [InlineData("")]
    public void Classify_rejects_unclassified_and_binary_extensions(string extension)
        => SkillResourceIndexer.Classify(extension).Should().BeNull();

    [Fact]
    public async Task IndexAsync_lists_classified_files_and_skips_noise()
    {
        var root = Path.Combine(_rootPath, "skill");
        WriteFile(root, "SKILL.md", "body");
        WriteFile(root, "scripts/deploy.ps1", "Write-Host");
        WriteFile(root, "image.png", "binary");
        WriteFile(root, "node_modules/dependency/index.js", "module");

        var index = await SkillResourceIndexer.IndexAsync(root, CancellationToken.None);

        index.Truncated.Should().BeFalse();
        index.Entries.Select(entry => entry.RelativePath)
            .Should().Equal("scripts/deploy.ps1", "SKILL.md");
        index.Entries.Select(entry => entry.Kind)
            .Should().Equal(SkillResourceKind.Script, SkillResourceKind.Resource);
    }

    [Fact]
    public async Task IndexAsync_stops_at_the_depth_limit()
    {
        var root = Path.Combine(_rootPath, "skill");
        WriteFile(root, "one.md", "1");
        WriteFile(root, "a/two.md", "2");
        WriteFile(root, "a/b/three.md", "3");
        WriteFile(root, "a/b/c/four.md", "4");

        var index = await SkillResourceIndexer.IndexAsync(root, CancellationToken.None);

        index.Entries.Select(entry => entry.RelativePath)
            .Should().Equal("a/b/three.md", "a/two.md", "one.md");
    }

    [Fact]
    public async Task IndexAsync_marks_truncation_at_the_entry_cap()
    {
        var root = Path.Combine(_rootPath, "skill");
        for (var index = 0; index < SkillResourceIndexer.MaximumEntries + 5; index++)
        {
            WriteFile(root, $"file-{index:0000}.md", "x");
        }

        var result = await SkillResourceIndexer.IndexAsync(root, CancellationToken.None);

        result.Truncated.Should().BeTrue();
        result.Entries.Should().HaveCount(SkillResourceIndexer.MaximumEntries);
    }

    [Fact]
    public async Task IndexAsync_skips_reparse_points()
    {
        var root = Path.Combine(_rootPath, "skill");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(_rootPath, "outside");
        WriteFile(outside, "escaped.md", "secret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Creating a link needs Developer Mode or SeCreateSymbolicLinkPrivilege; the guard itself is
            // still exercised by SkillPathGuard on every read, so this case is environment-dependent.
            return;
        }

        var index = await SkillResourceIndexer.IndexAsync(root, CancellationToken.None);

        index.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexAsync_returns_an_empty_index_for_a_missing_directory()
    {
        var index = await SkillResourceIndexer.IndexAsync(
            Path.Combine(_rootPath, "absent"), CancellationToken.None);

        index.Entries.Should().BeEmpty();
        index.Truncated.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
