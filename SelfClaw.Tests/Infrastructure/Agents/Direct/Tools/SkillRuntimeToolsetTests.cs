using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class SkillRuntimeToolsetTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateTools_returns_no_tools_for_an_empty_effective_set()
    {
        new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([], []).Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_skill_returns_an_explicit_failed_result_through_the_sdk_marshaller()
    {
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([CreateSkill("review", "instructions")], []);
        var result = await FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName)
            .InvokeAsync(new AIFunctionArguments { ["skillId"] = "missing" });
        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
    }

    [Fact]
    public async Task ActivateSkill_is_idempotent_and_enforces_the_per_turn_limit()
    {
        var skills = Enumerable.Range(1, 6)
            .Select(index => CreateSkill($"skill-{index}", $"content-{index}"))
            .ToArray();
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools(
            skills,
            ["skill-1", "skill-2", "skill-3", "skill-4"]);
        var activate = FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName);

        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-1" }))
            .Should().Contain("already activated");
        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-5" }))
            .Should().StartWith("content-5");
        (await InvokeStringAsync(activate, new() { ["skillId"] = "skill-6" }))
            .Should().Contain("limit of 5");
    }

    [Fact]
    public async Task ActivateSkill_returns_the_skill_body_with_the_resource_and_script_manifest()
    {
        var skill = CreateSkill(
            "review",
            "instructions",
            ("references/api.md", "guide"),
            ("scripts/deploy.ps1", "Write-Host"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);

        var result = await InvokeStringAsync(
            FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName),
            new() { ["skillId"] = "review" });

        result.Should().StartWith("instructions");
        result.Should().Contain("[SelfClaw Skill Resources: review]");
        result.Should().Contain("Resources (read with read_skill_resource):");
        result.Should().Contain("references/api.md");
        result.Should().Contain("Scripts (execute with run_skill_script; never transcribe or reimplement a script body):");
        result.Should().Contain("scripts/deploy.ps1");
    }

    [Fact]
    public async Task ActivateSkill_reports_an_empty_inventory()
    {
        var skill = CreateSkill("review", "instructions");
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);

        var result = await InvokeStringAsync(
            FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName),
            new() { ["skillId"] = "review" });

        result.Should().Contain("No resource or script files.");
    }

    [Fact]
    public async Task ActivateSkill_summarizes_and_flags_large_inventories()
    {
        var files = Enumerable.Range(1, 25)
            .Select(index => ($"references/page-{index:00}.md", "body"))
            .ToArray();
        var skill = CreateSkill("review", "instructions", files);
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);

        var result = await InvokeStringAsync(
            FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName),
            new() { ["skillId"] = "review" });

        result.Should().Contain("(+5 more not listed)");
        result.Should().NotContain("reached its entry limit");
    }

    [Fact]
    public async Task ActivateSkill_flags_a_truncated_inventory()
    {
        var files = Enumerable.Range(1, SkillResourceIndexer.MaximumEntries + 1)
            .Select(index => ($"references/page-{index:0000}.md", "body"))
            .ToArray();
        var skill = CreateSkill("review", "instructions", files);
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);

        var result = await InvokeStringAsync(
            FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName),
            new() { ["skillId"] = "review" });

        result.Should().Contain("The inventory reached its entry limit and may be incomplete.");
    }

    [Fact]
    public async Task ReadSkillResource_requires_activation_and_returns_a_bounded_page()
    {
        var skill = CreateSkill("review", "instructions");
        Directory.CreateDirectory(Path.Combine(skill.InstallPath, "references"));
        await File.WriteAllTextAsync(
            Path.Combine(skill.InstallPath, "references", "guide.md"),
            "one\ntwo\nthree\nfour");
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);
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
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], ["review"]);
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

    [Fact]
    public void CreateTools_binds_the_script_runner_as_an_approval_gated_run_tool()
    {
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([CreateSkill("review", "x")], []);

        var binding = tools.Single(item => item.Tool.Name == SkillRuntimeToolset.RunSkillScriptToolName);
        binding.Descriptor.Kind.Should().Be(ToolCallKind.Run);
        binding.Descriptor.SourceKind.Should().Be(ToolSourceKind.Skill);
        binding.Descriptor.DisplayName.Should().Be("Run skill script");
        binding.RequiresApproval.Should().BeTrue();

        var listBinding = tools.Single(item => item.Tool.Name == SkillRuntimeToolset.ListSkillResourcesToolName);
        listBinding.Descriptor.Kind.Should().Be(ToolCallKind.List);
        listBinding.Descriptor.SourceKind.Should().Be(ToolSourceKind.Skill);
        listBinding.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public async Task ListSkillResources_pages_the_inventory_and_labels_each_kind()
    {
        var skill = CreateSkill(
            "review",
            "instructions",
            ("references/api.md", "guide"),
            ("references/tuning.json", "{}"),
            ("references/notes.txt", "notes"),
            ("scripts/deploy.ps1", "exit 0"),
            ("scripts/check.py", "print(1)"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], ["review"]);
        var list = FindFunction(tools, SkillRuntimeToolset.ListSkillResourcesToolName);

        var result = await InvokeResultAsync(list, new() { ["skillId"] = "review", ["count"] = 2 });

        result.Summary.Should().Be("Listed 2 of 5 Skill resources.");
        result.Detail.Should().Contain("showing 1-2 of 5");
        result.Detail.Should().Contain("[resource] references/api.md");
        result.Detail.Should().Contain("Use startIndex 2 to continue.");
    }

    [Fact]
    public async Task ListSkillResources_requires_activation()
    {
        var skill = CreateSkill("review", "instructions", ("references/api.md", "guide"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);

        var result = await InvokeResultAsync(
            FindFunction(tools, SkillRuntimeToolset.ListSkillResourcesToolName),
            new() { ["skillId"] = "review" });

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Detail.Should().Contain("not activated");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/Windows")]
    public async Task ListSkillResources_rejects_an_escaping_directory(string relativeDirectory)
    {
        var skill = CreateSkill("review", "instructions", ("references/api.md", "guide"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], ["review"]);
        var list = FindFunction(tools, SkillRuntimeToolset.ListSkillResourcesToolName);

        Func<Task> action = async () => await list.InvokeAsync(new AIFunctionArguments
        {
            ["skillId"] = "review",
            ["relativeDirectory"] = relativeDirectory
        });

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ListSkillResources_scopes_to_a_subdirectory()
    {
        var skill = CreateSkill(
            "review",
            "instructions",
            ("references/api.md", "guide"),
            ("scripts/deploy.ps1", "exit 0"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], ["review"]);

        var result = await InvokeResultAsync(
            FindFunction(tools, SkillRuntimeToolset.ListSkillResourcesToolName),
            new() { ["skillId"] = "review", ["relativeDirectory"] = "references" });

        result.Summary.Should().Be("Listed 1 of 1 Skill resources.");
        result.Detail.Should().Contain("references/api.md");
        result.Detail.Should().NotContain("deploy.ps1");
    }

    [Fact]
    public async Task RunSkillScript_requires_activation_and_reports_the_exit_code()
    {
        var skill = CreateSkill(
            "review",
            "instructions",
            ("scripts/ok.ps1", "exit 0"),
            ("scripts/bad.ps1", "exit 4"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], []);
        var run = FindFunction(tools, SkillRuntimeToolset.RunSkillScriptToolName);

        (await InvokeStringAsync(run, new() { ["skillId"] = "review", ["relativePath"] = "scripts/ok.ps1" }))
            .Should().Contain("not activated");

        await InvokeStringAsync(
            FindFunction(tools, SkillRuntimeToolset.ActivateSkillToolName),
            new() { ["skillId"] = "review" });

        var succeeded = await run.InvokeAsync(new AIFunctionArguments
        {
            ["skillId"] = "review",
            ["relativePath"] = "scripts/ok.ps1"
        });
        succeeded.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Completed);

        var failed = await run.InvokeAsync(new AIFunctionArguments
        {
            ["skillId"] = "review",
            ["relativePath"] = "scripts/bad.ps1"
        });
        failed.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
    }

    [Fact]
    public async Task ReadSkillResource_labels_a_script_but_not_a_resource()
    {
        var skill = CreateSkill(
            "review",
            "instructions",
            ("references/guide.md", "one\ntwo"),
            ("scripts/deploy.ps1", "param()\nexit 0"));
        var tools = new SkillRuntimeToolset(new SkillScriptRunner()).CreateTools([skill], ["review"]);
        var read = FindFunction(tools, SkillRuntimeToolset.ReadSkillResourceToolName);

        var script = await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "scripts/deploy.ps1"
        });
        script.Should().Contain("scripts/deploy.ps1 [script]");
        script.Should().Contain($"run it with {SkillRuntimeToolset.RunSkillScriptToolName}");

        var resource = await InvokeStringAsync(read, new()
        {
            ["skillId"] = "review",
            ["relativePath"] = "references/guide.md"
        });
        resource.Should().StartWith("references/guide.md (lines 1-2 of 2)");
        resource.Should().NotContain("[script]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private ResolvedSkill CreateSkill(
        string id,
        string content,
        params (string RelativePath, string Content)[] files)
    {
        var installPath = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(installPath);
        foreach (var file in files)
        {
            var path = Path.Combine(installPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }

        var index = SkillResourceIndexer.IndexAsync(installPath, CancellationToken.None).GetAwaiter().GetResult();
        return new ResolvedSkill(id, id, $"{id} description", [], installPath, content, index);
    }

    private static AIFunction FindFunction(IReadOnlyList<DirectToolBinding> tools, string name)
        => tools.Single(binding => binding.Tool.Name == name).Tool;

    private static async Task<DirectToolResult> InvokeResultAsync(
        AIFunction function,
        AIFunctionArguments arguments)
        => (await function.InvokeAsync(arguments)).Should().BeOfType<DirectToolResult>().Which;

    private static async Task<string> InvokeStringAsync(
        AIFunction function,
        AIFunctionArguments arguments)
    {
        var result = await function.InvokeAsync(arguments);
        return result.Should().BeOfType<DirectToolResult>().Which.Content.GetString() ?? string.Empty;
    }
}
