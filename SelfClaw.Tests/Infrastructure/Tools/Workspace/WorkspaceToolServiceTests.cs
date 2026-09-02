using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Tests.Infrastructure.Tools.Workspace;

public sealed class WorkspaceToolServiceTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceToolService _service = new();

    public WorkspaceToolServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(Path.Combine(_rootPath, "src"));
        File.WriteAllText(Path.Combine(_rootPath, "src", "demo.md"), "# Hello\n\nstreamed markdown");
    }

    [Fact]
    public async Task Read_file_returns_expected_relative_path_and_content()
    {
        var result = await _service.ReadFileAsync(_rootPath, "src/demo.md");

        result.RelativePath.Should().Be(Path.Combine("src", "demo.md"));
        result.Content.Should().Contain("streamed markdown");
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Search_text_finds_matching_lines()
    {
        var hits = await _service.SearchTextAsync(_rootPath, "markdown");

        hits.Should().ContainSingle();
        hits[0].RelativePath.Should().Be(Path.Combine("src", "demo.md"));
        hits[0].LineText.Should().Contain("markdown");
    }

    [Fact]
    public async Task Search_text_glob_scopes_to_matching_files()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "notes.txt"), "markdown mentioned here too");

        var mdOnly = await _service.SearchTextAsync(
            _rootPath, "markdown", new WorkspaceSearchOptions { Glob = "**/*.md" });

        mdOnly.Should().OnlyContain(hit => hit.RelativePath.EndsWith(".md"));
        mdOnly.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_text_relative_path_scopes_to_subdirectory()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "docs"));
        File.WriteAllText(Path.Combine(_rootPath, "docs", "guide.md"), "markdown lives in docs");

        var srcOnly = await _service.SearchTextAsync(
            _rootPath, "markdown", new WorkspaceSearchOptions { RelativePath = "src" });

        srcOnly.Should().OnlyContain(hit => hit.RelativePath.StartsWith("src"));
    }

    [Fact]
    public async Task Search_text_regex_matches_pattern()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "codes.txt"), "id ABC123 done");

        var hits = await _service.SearchTextAsync(
            _rootPath, @"[A-Z]{3}\d{3}", new WorkspaceSearchOptions { IsRegex = true });

        hits.Should().Contain(hit => hit.LineText.Contains("ABC123"));
    }

    [Fact]
    public async Task Search_text_case_sensitive_excludes_other_casings()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "casing.txt"), "MARKDOWN uppercase");

        var caseSensitive = await _service.SearchTextAsync(
            _rootPath, "MARKDOWN", new WorkspaceSearchOptions { CaseSensitive = true });

        caseSensitive.Should().OnlyContain(hit => hit.LineText.Contains("MARKDOWN"));
        caseSensitive.Should().NotContain(hit => hit.RelativePath.EndsWith("demo.md"));
    }

    [Fact]
    public async Task Search_text_max_results_caps_hits()
    {
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(_rootPath, "src", $"hit{index}.txt"), "needle");
        }

        var hits = await _service.SearchTextAsync(
            _rootPath, "needle", new WorkspaceSearchOptions { MaxResults = 2 });

        hits.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task List_files_skips_build_and_dependency_directories()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "bin"));
        File.WriteAllText(Path.Combine(_rootPath, "bin", "artifact.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(_rootPath, "node_modules"));
        File.WriteAllText(Path.Combine(_rootPath, "keep.txt"), "content");

        var entries = await _service.ListFilesAsync(_rootPath, null);

        entries.Should().NotContain(entry => entry.RelativePath == "bin");
        entries.Should().NotContain(entry => entry.RelativePath == "node_modules");
        entries.Should().Contain(entry => entry.RelativePath == "keep.txt");
    }

    [Fact]
    public async Task List_files_reports_size_for_files_and_null_for_directories()
    {
        var entries = await _service.ListFilesAsync(_rootPath, null);

        var srcDir = entries.Single(entry => entry.RelativePath == "src");
        srcDir.IsDirectory.Should().BeTrue();
        srcDir.SizeBytes.Should().BeNull();
    }


    [Fact]
    public async Task Write_file_creates_and_overwrites_text()
    {
        var created = await _service.WriteFileAsync(_rootPath, "src/generated.txt", "first pass");
        var overwritten = await _service.WriteFileAsync(_rootPath, "src/generated.txt", "second pass");

        created.Applied.Should().BeTrue();
        created.OverwroteExisting.Should().BeFalse();
        overwritten.Applied.Should().BeTrue();
        overwritten.OverwroteExisting.Should().BeTrue();
        File.ReadAllText(Path.Combine(_rootPath, "src", "generated.txt")).Should().Be("second pass");
    }

    [Fact]
    public async Task Shell_command_returns_output_and_exit_code()
    {
        var result = await _service.RunShellCommandAsync(_rootPath, "Write-Output 'hello from powershell'", 30);

        result.Executed.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("hello from powershell");
    }

    [Fact]
    public async Task Read_file_line_range_returns_only_requested_window()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"line {index}"));
        File.WriteAllText(Path.Combine(_rootPath, "src", "many.txt"), lines);

        var window = await _service.ReadFileAsync(_rootPath, "src/many.txt", startLine: 5, lineCount: 3);

        window.StartLine.Should().Be(5);
        window.EndLine.Should().Be(7);
        window.TotalLines.Should().Be(20);
        window.Content.Should().Be("line 5\nline 6\nline 7");
        window.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Read_file_whole_file_reports_total_lines()
    {
        var content = await _service.ReadFileAsync(_rootPath, "src/demo.md");

        content.StartLine.Should().Be(1);
        content.TotalLines.Should().Be(3);
        content.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Glob_files_matches_by_pattern_and_excludes_others()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "service.cs"), "class Service {}");
        File.WriteAllText(Path.Combine(_rootPath, "src", "readme.txt"), "notes");

        var matches = await _service.GlobFilesAsync(_rootPath, "**/*.cs");

        matches.Should().OnlyContain(entry => entry.RelativePath.EndsWith(".cs"));
        matches.Should().Contain(entry => entry.RelativePath.EndsWith("service.cs"));
    }

    [Fact]
    public async Task Edit_file_replaces_single_occurrence()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "edit.txt"), "alpha beta gamma");

        var result = await _service.EditFileAsync(_rootPath, "src/edit.txt", "beta", "BETA");

        result.Applied.Should().BeTrue();
        File.ReadAllText(Path.Combine(_rootPath, "src", "edit.txt")).Should().Be("alpha BETA gamma");
    }

    [Fact]
    public async Task Edit_file_refuses_ambiguous_match_without_replace_all()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "dup.txt"), "x x x");

        var result = await _service.EditFileAsync(_rootPath, "src/dup.txt", "x", "y");

        result.Applied.Should().BeFalse();
        File.ReadAllText(Path.Combine(_rootPath, "src", "dup.txt")).Should().Be("x x x");
    }

    [Fact]
    public async Task Edit_file_replace_all_replaces_every_occurrence()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "dup.txt"), "x x x");

        var result = await _service.EditFileAsync(_rootPath, "src/dup.txt", "x", "y", replaceAll: true);

        result.Applied.Should().BeTrue();
        File.ReadAllText(Path.Combine(_rootPath, "src", "dup.txt")).Should().Be("y y y");
    }

    [Fact]
    public async Task Edit_file_reports_missing_text()
    {
        File.WriteAllText(Path.Combine(_rootPath, "src", "edit.txt"), "alpha beta");

        var result = await _service.EditFileAsync(_rootPath, "src/edit.txt", "missing", "x");

        result.Applied.Should().BeFalse();
    }

    [Fact]
    public async Task Edit_file_matches_when_file_uses_crlf_and_old_text_uses_lf()
    {
        // Windows files under git autocrlf are CRLF; the model emits LF in oldText.
        File.WriteAllBytes(
            Path.Combine(_rootPath, "src", "crlf.txt"),
            System.Text.Encoding.UTF8.GetBytes("alpha\r\n beta\r\n gamma\r\n"));

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/crlf.txt",
            "beta\r\n gamma",
            "BETA\r\n GAMMA");

        result.Applied.Should().BeTrue();
        var written = File.ReadAllText(Path.Combine(_rootPath, "src", "crlf.txt"));
        // On-disk CRLF convention is preserved end-to-end (no bare LF leaked back).
        written.Should().Be("alpha\r\n BETA\r\n GAMMA\r\n");
    }

    [Fact]
    public async Task Edit_file_matches_lf_old_text_against_crlf_file()
    {
        // The model emits LF only; the file is CRLF. The match must still succeed
        // because edit_file normalizes both sides to LF before searching.
        File.WriteAllBytes(
            Path.Combine(_rootPath, "src", "crlf2.txt"),
            System.Text.Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three\r\n"));

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/crlf2.txt",
            "line two\nline three",
            "TWO\nTHREE");

        result.Applied.Should().BeTrue();
        var written = File.ReadAllBytes(Path.Combine(_rootPath, "src", "crlf2.txt"));
        // On-disk CRLF convention is preserved.
        System.Text.Encoding.UTF8.GetString(written).Should().Be("line one\r\nTWO\r\nTHREE\r\n");
    }

    [Fact]
    public async Task Edit_file_fuzzy_matches_internal_whitespace_drift()
    {
        // The file has two spaces between `return` and `42`; the model's oldText has
        // one. The exact substring search fails, so the line-block fallback aligns
        // the whole line via whitespace-insensitive signatures.
        File.WriteAllText(
            Path.Combine(_rootPath, "src", "drift.txt"),
            "def example():\n    return  42\n");

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/drift.txt",
            "    return 42",
            "    return 43");

        result.Applied.Should().BeTrue();
        File.ReadAllText(Path.Combine(_rootPath, "src", "drift.txt"))
            .Should().Be("def example():\n    return 43\n");
    }

    [Fact]
    public async Task Edit_file_fuzzy_matches_tab_vs_space_indent()
    {
        File.WriteAllText(
            Path.Combine(_rootPath, "src", "tabs.txt"),
            "def example():\n\treturn 42\n");

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/tabs.txt",
            "    return 42",
            "    return 43");

        result.Applied.Should().BeTrue();
        File.ReadAllText(Path.Combine(_rootPath, "src", "tabs.txt"))
            .Should().Be("def example():\n    return 43\n");
    }

    [Fact]
    public async Task Edit_file_fuzzy_refuses_ambiguous_line_block()
    {
        File.WriteAllText(
            Path.Combine(_rootPath, "src", "ambig.txt"),
            "return 1\nreturn 2\n");

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/ambig.txt",
            "return",
            "yield");

        result.Applied.Should().BeFalse();
    }

    [Fact]
    public async Task Edit_file_not_found_message_reports_closest_lines()
    {
        File.WriteAllText(
            Path.Combine(_rootPath, "src", "diag.txt"),
            "import os\nfrom pathlib import Path\n\ndef main():\n    pass\n");

        var result = await _service.EditFileAsync(
            _rootPath,
            "src/diag.txt",
            "def example():",
            "def example(): pass");

        result.Applied.Should().BeFalse();
        result.Message.Should().Contain("closest lines");
        result.Message.Should().Contain("def main()");
    }

    [Fact]
    public async Task Read_file_normalizes_crlf_to_lf_for_consistent_view()
    {
        File.WriteAllBytes(
            Path.Combine(_rootPath, "src", "crlf-read.txt"),
            System.Text.Encoding.UTF8.GetBytes("alpha\r\n beta\r\n"));

        var content = await _service.ReadFileAsync(_rootPath, "src/crlf-read.txt");

        content.Content.Should().NotContain("\r");
        content.Content.Should().Be("alpha\n beta\n");
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        var action = () => _service.ReadFileAsync(_rootPath, "..\\outside.txt");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }
}
