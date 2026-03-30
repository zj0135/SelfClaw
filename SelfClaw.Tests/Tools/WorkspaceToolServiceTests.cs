using FluentAssertions;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Tests.Tools;

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
