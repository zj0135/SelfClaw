using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class SkillPackageReaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_extracts_front_matter_and_preserves_body()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, "SKILL.md");
        const string body = "# Review\r\n\r\nKeep this body unchanged.\r\n";
        // ReplaceLineEndings normalizes first, so this stays correct whether the raw string literal is
        // checked out with LF or CRLF.
        var markdown = """
            ---
            name: Code/Review
            description: >-
              Reviews changes
              before commit.
            version: "1.2.3"
            triggers:
              - review
              - "check diff"
            ---
            """.ReplaceLineEndings("\r\n") + "\r\n" + body;
        await File.WriteAllTextAsync(path, markdown);
        var reader = new SkillPackageReader(CreateLimits());

        var result = await reader.ReadAsync(path);

        result.Id.Should().Be("code/review");
        result.Name.Should().Be("Code/Review");
        result.Description.Should().Be("Reviews changes before commit.");
        result.Version.Should().Be("1.2.3");
        result.Triggers.Should().Equal("review", "check diff");
        result.Content.Should().Be(markdown);
    }

    [Theory]
    [InlineData("missing-description", "---\nname: review\n---\nBody")]
    [InlineData("invalid-name", "---\nname: ../review\ndescription: Review\n---\nBody")]
    [InlineData("missing-front-matter", "# Review")]
    public async Task ReadAsync_rejects_invalid_manifests(string _, string markdown)
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, "SKILL.md");
        await File.WriteAllTextAsync(path, markdown);
        var reader = new SkillPackageReader(CreateLimits());

        var action = () => reader.ReadAsync(path);

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private static ExtensionPackageLimits CreateLimits()
        => new(1024 * 1024, 1024 * 1024, 100, 512 * 1024, 256 * 1024);
}
