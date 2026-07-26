using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class SkillRuntimeToolsetTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateTools_returns_no_tools_for_an_empty_effective_set()
    {
        new SkillRuntimeToolset().CreateTools([], []).Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateSkill_is_idempotent_and_enforces_the_per_turn_limit()
    {
        var skills = Enumerable.Range(1, 6)
            .Select(index => CreateSkill($"skill-{index}", $"content-{index}"))
            .ToArray();
        var tools = new SkillRuntimeToolset().CreateTools(
            skills,
            ["skill-1", "skill-2", "skill-3", "skill-4"]);
        var activate = FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName);

        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-1" }))
            .Should().Contain("already activated");
        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-5" }))
            .Should().Be("content-5");
        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-6" }))
            .Should().Contain("limit of 5");
    }

    [Fact]
    public async Task ReadSkillResource_requires_activation_and_returns_a_bounded_page()
    {
        var skill = CreateSkill("review", "instructions");
        Directory.CreateDirectory(Path.Combine(skill.InstallPath, "references"));
        await File.WriteAllTextAsync(
            Path.Combine(skill.InstallPath, "references", "guide.md"),
            "one\ntwo\nthree\nfour");
        var tools = new SkillRuntimeToolset().CreateTools([skill], []);
        var activate = FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName);
        var read = FindFunction(tools, SkillRuntimeToolset.ReadSkillResourceToolName);

        (await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "references/guide.md"
        })).Should().Contain("not activated");
        await InvokeStringAsync(activate, new() { ["skillId"] = "review" });
        var page = await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "references/guide.md",
            ["startLine"] = 2,
            ["lineCount"] = 2
        });

        page.Should().Be("references/guide.md (lines 2-3 of 4)\ntwo\nthree");
    }

    [Fact]
    public async Task ReadSkillResource_rejects_escapes_binary_files_and_oversized_text()
    {
        var skill = CreateSkill("review", "instructions");
        await File.WriteAllBytesAsync(Path.Combine(skill.InstallPath, "image.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(
            Path.Combine(skill.InstallPath, "large.txt"),
            new string('x', 1024 * 1024 + 1));
        var tools = new SkillRuntimeToolset().CreateTools([skill], ["review"]);
        var read = FindFunction(tools, SkillRuntimeToolset.ReadSkillResourceToolName);

        Func<Task> escapeAction = async () => await read.InvokeAsync(new AIFunctionArguments
        {
            ["skillId"] = "review",
            ["relativePath"] = "../outside.md"
        });
        await escapeAction.Should().ThrowAsync<InvalidDataException>();
        (await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "image.png"
        })).Should().Contain("not an allowed text file type");
        (await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "large.txt"
        })).Should().Contain("exceeds the 1048576 byte limit");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private ResolvedSkill CreateSkill(string id, string content)
    {
        var installPath = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(installPath);
        return new ResolvedSkill(id, id, $"{id} description", [], installPath, content);
    }

    private static AIFunction FindFunction(IReadOnlyList<AITool> tools, string name)
        => tools.Cast<AIFunction>().Single(tool => tool.Name == name);

    private static async Task<string> InvokeStringAsync(
        AIFunction function,
        AIFunctionArguments arguments)
    {
        var result = await function.InvokeAsync(arguments);
        return result.Should().BeOfType<JsonElement>().Which.GetString()!;
    }
}
