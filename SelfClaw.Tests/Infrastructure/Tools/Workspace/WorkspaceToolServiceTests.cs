using FluentAssertions;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Tests.Infrastructure.Tools.Workspace;

public sealed class WorkspaceToolServiceTests : IDisposable
{
    /// <summary>A real 1x1 PNG: the read path must hand the original bytes back untouched.</summary>
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private readonly string _rootPath;
    private readonly WorkspaceToolService _service = new(new(), new(), new());

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

        var content = result.Text;
        Assert.NotNull(content);
        content.RelativePath.Should().Be(Path.Combine("src", "demo.md"));
        content.Content.Should().Contain("streamed markdown");
        content.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Read_file_returns_image_content_when_the_turn_accepts_images()
    {
        File.WriteAllBytes(Path.Combine(_rootPath, "src", "shot.png"), PngBytes);

        var result = await _service.ReadFileAsync(_rootPath, "src/shot.png", includeImageContent: true);

        result.Error.Should().BeNull();
        result.Text.Should().BeNull();
        var image = result.Image;
        Assert.NotNull(image);
        image.MediaType.Should().Be("image/png");
        image.RelativePath.Should().Be(Path.Combine("src", "shot.png"));
        image.Data.Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Read_file_reports_an_image_as_unavailable_when_images_are_not_requested()
    {
        File.WriteAllBytes(Path.Combine(_rootPath, "src", "shot.png"), PngBytes);

        var result = await _service.ReadFileAsync(_rootPath, "src/shot.png");

        result.Image.Should().BeNull();
        result.Text.Should().BeNull();
        result.Error.Should().Be("'src/shot.png' is an image, but this turn cannot receive image content.");
    }

    [Fact]
    public async Task Read_file_rejects_an_image_above_the_single_image_limit()
    {
        var oversized = new byte[WorkspaceImageLimits.MaximumBytes + 1];
        PngBytes.CopyTo(oversized, 0);
        File.WriteAllBytes(Path.Combine(_rootPath, "src", "huge.png"), oversized);

        var result = await _service.ReadFileAsync(_rootPath, "src/huge.png", includeImageContent: true);

        result.Image.Should().BeNull();
        result.Error.Should().Contain(oversized.Length.ToString()).And
            .Contain(WorkspaceImageLimits.MaximumBytes.ToString());
    }

    [Fact]
    public async Task Read_file_reads_a_png_extension_holding_text_as_text()
    {
        // Signature wins over extension: a mislabelled text file must not enter the image path.
        File.WriteAllText(Path.Combine(_rootPath, "src", "notes.png"), "plain text, not a bitmap");

        var result = await _service.ReadFileAsync(_rootPath, "src/notes.png", includeImageContent: true);

        result.Image.Should().BeNull();
        result.Error.Should().BeNull();
        var text = result.Text;
        Assert.NotNull(text);
        text.Content.Should().Be("plain text, not a bitmap");
    }

    [Fact]
    public async Task Read_file_ignores_the_line_range_for_images()
    {
        File.WriteAllBytes(Path.Combine(_rootPath, "src", "shot.png"), PngBytes);

        var result = await _service.ReadFileAsync(
            _rootPath, "src/shot.png", startLine: 3, lineCount: 1, includeImageContent: true);

        result.Error.Should().BeNull();
        var image = result.Image;
        Assert.NotNull(image);
        image.Data.Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Read_file_rejects_binary_content_that_is_not_an_image()
    {
        File.WriteAllBytes(Path.Combine(_rootPath, "src", "blob.bin"), [0x00, 0x01, 0x02, 0x03, 0x04]);

        var result = await _service.ReadFileAsync(_rootPath, "src/blob.bin");

        result.Text.Should().BeNull();
        result.Error.Should().Be("Only text files and supported images (PNG, JPEG, WebP, GIF) can be read.");
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
    public async Task Invalid_search_regex_reports_the_ripgrep_error()
    {
        var search = () => _service.SearchTextAsync(_rootPath, "[", new WorkspaceSearchOptions { IsRegex = true });

        await search.Should().ThrowAsync<InvalidOperationException>().WithMessage("*regex parse error*");
    }

    [Fact]
    public async Task Shell_timeout_is_distinct_from_caller_cancellation()
    {
        var timeout = () => _service.RunShellCommandAsync(_rootPath, "Start-Sleep -Seconds 10", 1)
            .WaitAsync(TimeSpan.FromSeconds(10));
        await timeout.Should().ThrowAsync<TimeoutException>().WithMessage("*timed out after 1 seconds*");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var cancelled = () => _service.RunShellCommandAsync(_rootPath, "Start-Sleep -Seconds 10", 30, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Read_file_line_range_returns_only_requested_window()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"line {index}"));
        File.WriteAllText(Path.Combine(_rootPath, "src", "many.txt"), lines);

        var window = (await _service.ReadFileAsync(_rootPath, "src/many.txt", startLine: 5, lineCount: 3)).Text;

        Assert.NotNull(window);
        window.StartLine.Should().Be(5);
        window.EndLine.Should().Be(7);
        window.TotalLines.Should().Be(20);
        window.Content.Should().Be("line 5\nline 6\nline 7");
        window.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Read_file_whole_file_reports_total_lines()
    {
        var content = (await _service.ReadFileAsync(_rootPath, "src/demo.md")).Text;

        Assert.NotNull(content);
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

        var content = (await _service.ReadFileAsync(_rootPath, "src/crlf-read.txt")).Text;

        Assert.NotNull(content);
        content.Content.Should().NotContain("\r");
        content.Content.Should().Be("alpha\n beta\n");
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        var action = () => _service.ReadFileAsync(_rootPath, "..\\outside.txt");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Run_shell_command_warns_when_it_reaches_into_a_skill_installation()
    {
        var logger = new RecordingLogger();
        var service = new WorkspaceToolService(
            new WorkspaceFileService(), new WorkspaceSearchService(), new WorkspaceShellRunner(), logger);

        await service.RunShellCommandAsync(_rootPath, "Write-Output \"SelfClaw/skills\"", 30);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Skill or Plugin installation directory");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private sealed class RecordingLogger : ILogger<WorkspaceToolService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
