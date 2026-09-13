using FluentAssertions;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Tests.Infrastructure.Tools.Workspace;

public sealed class WorkspaceGlobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceSearchService _search = new();

    public WorkspaceGlobTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("src/**/x.cs", new[] { "src/x.cs", "src/deep/x.cs" })]
    [InlineData("src\\**\\x.cs", new[] { "src/x.cs", "src/deep/x.cs" })]
    [InlineData("SRC/**/X.CS", new[] { "src/x.cs", "src/deep/x.cs" })]
    [InlineData("src/*.cs", new[] { "src/x.cs", "src/prefixx.cs", "src/Upper.CS" })]
    [InlineData("src/?.cs", new[] { "src/x.cs" })]
    [InlineData("*.cs", new[] { "root.cs" })]
    public async Task Glob_respects_directory_boundaries_wildcards_separators_and_case(string pattern, string[] expected)
    {
        foreach (var name in new[] { "root.cs", "src/x.cs", "src/deep/x.cs", "src/prefixx.cs", "src/Upper.CS" }) CreateFile(name);

        var matches = await _search.GlobFilesAsync(_root, pattern);

        matches.Select(entry => entry.RelativePath.Replace('\\', '/')).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Scoped_glob_still_matches_workspace_relative_paths()
    {
        CreateFile("src/x.cs");
        CreateFile("other/x.cs");
        var matches = await _search.GlobFilesAsync(_root, "src/**/x.cs", "src");
        matches.Should().ContainSingle().Which.RelativePath.Replace('\\', '/').Should().Be("src/x.cs");
    }

    [Fact]
    public async Task Glob_preserves_hidden_file_and_ignore_rules_but_excludes_hidden_and_dependency_directories()
    {
        foreach (var name in new[] { "visible.cs", "ignored.cs", ".hidden.cs", ".cache/hidden.cs", "src/bin/build.cs", "src/OBJ/build.cs", "secret/hidden.cs" }) CreateFile(name);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.cs\n");
        File.SetAttributes(Path.Combine(_root, "secret"), FileAttributes.Directory | FileAttributes.Hidden);

        var matches = await _search.GlobFilesAsync(_root, "**/*.cs");

        matches.Select(entry => entry.RelativePath).Should().BeEquivalentTo("visible.cs", "ignored.cs", ".hidden.cs");
    }

    [Fact]
    public async Task Glob_returns_the_newest_250_matches_with_stable_path_ties()
    {
        var now = DateTime.UtcNow;
        for (var index = 0; index < 300; index++)
        {
            var path = CreateFile($"file-{index:D3}.txt");
            File.SetLastWriteTimeUtc(path, now.AddSeconds(index));
        }

        var matches = await _search.GlobFilesAsync(_root, "*.txt");

        matches.Should().HaveCount(250);
        matches[0].RelativePath.Should().Be("file-299.txt");
        matches[^1].RelativePath.Should().Be("file-050.txt");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "file-298.txt"), now.AddSeconds(300));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "file-299.txt"), now.AddSeconds(300));
        var tied = await _search.GlobFilesAsync(_root, "*.txt");
        tied.Take(2).Select(entry => entry.RelativePath).Should().Equal("file-298.txt", "file-299.txt");
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, "content");
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
