using System.Runtime.CompilerServices;
using FluentAssertions;
using SelfClaw.LlmProvider.Client.Implementations;

namespace SelfClaw.Tests.LlmProvider;

/// <summary>
/// 目录约定的硬约束：按能力分目录、每个能力目录有 README、根目录不放代码、
/// 每个能力目录内部按 抽象/数据/实现 三层分桶。
/// 这些规则一旦只写在文档里就会腐坏，所以用测试钉住。
/// </summary>
public sealed class LlmProviderLayoutTests
{
    private static readonly string[] Buckets = ["Abstractions", "Dtos", "Implementations"];

    private static string LibraryRoot([CallerFilePath] string testFilePath = "")
    {
        // <repo>/SelfClaw.Tests/LlmProvider/<this file>
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(repoRoot, "SelfClaw.LlmProvider");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static IEnumerable<string> CodeDirectories()
    {
        var root = LibraryRoot();
        return Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(directory => !IsBuildOutput(directory + Path.DirectorySeparatorChar))
            .Where(directory => Directory.GetFiles(directory, "*.cs").Length > 0);
    }

    [Fact]
    public void 项目根目录不放代码文件()
    {
        var root = LibraryRoot();
        Directory.Exists(root).Should().BeTrue($"the library must live at {root}");

        Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Should()
            .BeEmpty("every source file belongs to a capability folder");
    }

    [Fact]
    public void 文档只有根目录一份README()
    {
        // 子目录 README 会碎片化，全部内容汇总到根 README。
        var root = LibraryRoot();
        var readmes = Directory
            .GetFiles(root, "README.md", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file + Path.DirectorySeparatorChar))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        readmes.Should().Equal("README.md");
    }

    [Fact]
    public void 根README覆盖每一条能力()
    {
        var root = LibraryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        var capabilities = Directory
            .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Where(directory => !IsBuildOutput(directory + Path.DirectorySeparatorChar))
            .Select(directory => Path.GetFileName(directory))
            .ToArray();

        capabilities.Should().NotBeEmpty();
        foreach (var capability in capabilities)
        {
            readme.Should().Contain($"### {capability} —", $"the root README documents {capability}");
        }
    }

    [Fact]
    public void 每个目录是一个独立命名空间()
    {
        var root = LibraryRoot();
        foreach (var directory in CodeDirectories())
        {
            var relative = Path.GetRelativePath(root, directory).Replace('\\', '.');
            var expected = $"namespace SelfClaw.LlmProvider.{relative};";

            Directory.GetFiles(directory, "*.cs").Should().OnlyContain(
                file => File.ReadAllText(file).Contains(expected, StringComparison.Ordinal),
                $"'{relative}' files declare their own namespace");
        }
    }

    [Fact]
    public void 能力目录内按抽象数据实现分桶()
    {
        var root = LibraryRoot();
        foreach (var capability in Directory
                     .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .Where(directory => !IsBuildOutput(directory + Path.DirectorySeparatorChar)))
        {
            var name = Path.GetFileName(capability);
            var files = Directory.GetFiles(capability, "*.cs");

            if (HomogeneousCapabilities.Contains(name, StringComparer.Ordinal))
            {
                // 同质叶子能力（只有一个种类）不强行分桶，但必须真的同质。
                files.Should().NotBeEmpty($"{name} is a code folder");
                files.Should().OnlyContain(
                    file => File.ReadAllText(file).Contains(": Exception", StringComparison.Ordinal) ||
                            File.ReadAllText(file).Contains(": LlmProviderException", StringComparison.Ordinal),
                    $"{name} holds only exception types");
                continue;
            }

            // 其余能力目录自身只放 README 与子目录，代码必须落在桶里。
            files.Should()
                .BeEmpty($"{name} must place code in Abstractions/Dtos/Implementations");
        }
    }

    [Fact]
    public void 非桶子目录是单一实现单元()
    {
        var root = LibraryRoot();
        foreach (var capability in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var child in Directory.EnumerateDirectories(capability))
            {
                if (Buckets.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                {
                    continue;
                }

                // Protocols/OpenAiChat 这类：整个目录就是一个实现单元，不该再出现接口或 DTO。
                Directory.GetFiles(child, "*.cs")
                    .Should()
                    .OnlyContain(
                        file => !File.ReadAllText(file).Contains("public interface", StringComparison.Ordinal),
                        $"{Path.GetFileName(child)} is one implementation unit");
            }
        }
    }

    private static readonly string[] HomogeneousCapabilities = ["Errors"];

    [Fact]
    public void 抽象桶只放接口数据桶只放纯数据()
    {
        var root = LibraryRoot();
        foreach (var directory in CodeDirectories())
        {
            var bucket = Path.GetFileName(directory);
            var files = Directory.GetFiles(directory, "*.cs");

            if (bucket == "Abstractions")
            {
                files.Should().OnlyContain(
                    file => File.ReadAllText(file).Contains("interface ", StringComparison.Ordinal),
                    "Abstractions holds interfaces");
            }

            if (bucket == "Dtos")
            {
                files.Should().NotContain(
                    file => File.ReadAllText(file).Contains(": DelegatingChatClient", StringComparison.Ordinal),
                    "Dtos holds data, not behaviour");
            }
        }
    }

    [Fact]
    public void 一个文件只声明一个类型()
    {
        // 仓库约定：一个文件只放一个 DTO 或一个服务类，绝不混放。
        var declaration = new System.Text.RegularExpressions.Regex(
            @"^(public|internal)\s+(sealed\s+|abstract\s+|static\s+|partial\s+)*(class|record|enum|interface|struct)\s+\w+",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var offenders = CodeDirectories()
            .SelectMany(directory => Directory.GetFiles(directory, "*.cs"))
            .Where(file => declaration.Matches(File.ReadAllText(file)).Count > 1)
            .Select(file => Path.GetRelativePath(LibraryRoot(), file))
            .ToArray();

        offenders.Should().BeEmpty("one type per file");
    }

    [Fact]
    public void 库不提供token计数或成本计算()
    {
        // 计数由 M.E.AI 的 UsageDetails 提供，价格表属于调用方配置。
        var root = LibraryRoot();
        var offending = Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file + Path.DirectorySeparatorChar))
            .Where(file =>
            {
                var name = Path.GetFileNameWithoutExtension(file);
                return name.Contains("TokenUsage", StringComparison.Ordinal) ||
                       name.Contains("CostCalculator", StringComparison.Ordinal);
            })
            .ToArray();

        offending.Should().BeEmpty();

        typeof(LlmClientFactory).Assembly.GetTypes()
            .Select(type => type.Name)
            .Should()
            .NotContain(name => name.Contains("TokenUsage", StringComparison.Ordinal))
            .And.NotContain(name => name.Contains("CostCalculator", StringComparison.Ordinal));
    }
}
