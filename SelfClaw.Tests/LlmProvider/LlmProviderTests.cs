using System.Reflection;
using SelfClaw.LlmProvider.Chat.Dtos;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Client.Abstractions;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Client.Implementations;
using SelfClaw.LlmProvider.Parameters.Dtos;
using SelfClaw.LlmProvider.Parameters.Implementations;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Profiles.Implementations;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using SelfClaw.LlmProvider.Protocols.Implementations;
using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Implementations;
using SelfClaw.LlmProvider.Transport.Implementations;
using SelfClaw.LlmProvider.Vendors.Dtos;
using SelfClaw.LlmProvider.Vendors.Implementations;
using SelfClaw.LlmProvider.Errors;
using FluentAssertions;

namespace SelfClaw.Tests.LlmProvider;

/// <summary>
/// 独立类库的硬约束：没有 SelfClaw 引用、没有持久化或 Windows 专属依赖、不带任何厂商数据。
/// 这些断言比文档里的承诺更可靠。
/// </summary>
public sealed class LlmProviderStandaloneTests
{
    private static readonly Assembly ProviderAssembly = typeof(LlmClientFactory).Assembly;

    [Fact]
    public void 程序集不引用任何SelfClaw程序集()
    {
        ProviderAssembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("SelfClaw.", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void 程序集不引用持久化加密或宿主程序集()
    {
        string[] forbidden =
        [
            "Microsoft.Data.Sqlite",
            "System.Security.Cryptography.ProtectedData",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Configuration",
            "Microsoft.Web.WebView2",
            "Microsoft.ML.Tokenizers"
        ];

        ProviderAssembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Should()
            .NotIntersectWith(forbidden);
    }

    [Fact]
    public void 目标框架不含Windows后缀()
    {
        var targetFramework = ProviderAssembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
        targetFramework.Should().NotBeNull();
        targetFramework!.FrameworkName.Should().NotContain("Windows");
    }

    [Fact]
    public void 程序集不内嵌任何厂商描述符()
    {
        ProviderAssembly.GetManifestResourceNames()
            .Should()
            .NotContain(name => name.Contains("Vendor", StringComparison.OrdinalIgnoreCase),
                "vendor data is product configuration and belongs to the caller");
    }
}

public sealed class LlmVendorCatalogTests
{
    [Fact]
    public void 空目录解析厂商时报可读错误并指出怎么加()
    {
        var act = () => new LlmVendorCatalog().GetRequired("openai");
        act.Should().Throw<LlmVendorCatalogException>()
            .WithMessage("*No vendor is registered*");
    }

    [Fact]
    public void 未知厂商列出已知厂商()
    {
        var act = () => TestVendors.CreateCatalog().GetRequired("nope");
        act.Should().Throw<LlmVendorCatalogException>()
            .WithMessage($"*nope*{TestVendors.Strict}*");
    }

    [Fact]
    public void 以代码加入的厂商可以解析()
    {
        TestVendors.CreateCatalog().GetRequired(TestVendors.Gateway).DisplayName.Should().Be("Gateway");
    }

    [Fact]
    public void 同名厂商后来者覆盖()
    {
        var catalog = TestVendors.CreateCatalog().AddFromJson("""
            {
              "vendorId": "test-strict",
              "displayName": "Overridden",
              "defaultEndpoint": "https://override.test/v1/",
              "protocols": { "openai-chat": {} }
            }
            """);

        catalog.GetRequired(TestVendors.Strict).DisplayName.Should().Be("Overridden");
        catalog.GetRequired(TestVendors.Strict).Protocols.Keys.Should().Equal(LlmProtocols.OpenAiChat);
    }

    [Fact]
    public void 描述符必须声明至少一个协议()
    {
        var act = () => new LlmVendorCatalog().AddFromJson("""
            { "vendorId": "broken", "displayName": "Broken",
              "defaultEndpoint": "https://broken.test/", "protocols": {} }
            """);
        act.Should().Throw<LlmVendorCatalogException>().WithMessage("*at least one protocol*");
    }

    [Fact]
    public void 描述符必须有绝对端点()
    {
        var act = () => new LlmVendorCatalog().AddFromJson("""
            { "vendorId": "broken", "displayName": "Broken",
              "defaultEndpoint": "not-a-uri", "protocols": { "openai-chat": {} } }
            """);
        act.Should().Throw<LlmVendorCatalogException>().WithMessage("*defaultEndpoint*");
    }

    [Fact]
    public void 目录不存在时保持为空而不是报错()
    {
        var catalog = new LlmVendorCatalog().AddFromDirectory(Path.Combine(Path.GetTempPath(), "no-such-vendors"));
        catalog.Count.Should().Be(0);
    }
}

/// <summary>
/// 参数表必须与硬编码的 if 链逐条等价。这里验证的是机制本身：来源链顺序、旧来源门控、值映射。
/// </summary>
public sealed class LlmParameterTableTests
{
    private const string ThinkingTypePath = "$.thinking.type";
    private const string ReasoningEffortPath = "$.reasoning_effort";
    private const string ParallelToolCallsPath = "$.parallel_tool_calls";

    [Fact]
    public void 严格厂商在没有显式选项时不注入思考字段()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Strict,
            TestVendors.CreateModel(hasTypedConfiguration: false));

        parameters.Should().NotContainKey(ThinkingTypePath);
    }

    [Fact]
    public void 网关厂商在没有配置时默认关闭思考()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(hasTypedConfiguration: false));

        parameters[ThinkingTypePath].GetString().Should().Be("disabled");
    }

    [Fact]
    public void 网关厂商在有思考等级时打开思考()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(hasTypedConfiguration: true, effort: "high"));

        parameters[ThinkingTypePath].GetString().Should().Be("enabled");
    }

    [Fact]
    public void 网关厂商的none等级映射成disabled()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(hasTypedConfiguration: true, effort: "none"));

        parameters[ThinkingTypePath].GetString().Should().Be("disabled");
    }

    [Fact]
    public void 严格厂商即使有思考等级也不写非标准字段()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Strict,
            TestVendors.CreateModel(hasTypedConfiguration: true, effort: "high"));

        parameters.Should().NotContainKey(ThinkingTypePath);
        parameters[ReasoningEffortPath].GetString().Should().Be("high");
    }

    [Fact]
    public void 手写思考选项在无类型化配置时生效()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                options: new() { ["thinking.type"] = "enabled" }));

        parameters[ThinkingTypePath].GetString().Should().Be("enabled");
    }

    [Fact]
    public void 手写思考选项被类型化配置门控()
    {
        var gated = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(
                hasTypedConfiguration: true,
                effort: null,
                options: new() { ["thinking.type"] = "enabled" }));
        gated.Should().NotContainKey(ThinkingTypePath);

        // 有思考等级时，来源链里 reasoning.effort 先命中，写出口径来自映射。
        var byEffort = TestVendors.Resolve(
            TestVendors.Gateway,
            TestVendors.CreateModel(
                hasTypedConfiguration: true,
                effort: "high",
                options: new() { ["thinking.type"] = "disabled" }));
        byEffort[ThinkingTypePath].GetString().Should().Be("enabled");
    }

    [Fact]
    public void 手写推理等级在无类型化配置时生效()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Strict,
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                options: new() { ["reasoning_effort"] = "low" }));

        parameters[ReasoningEffortPath].GetString().Should().Be("low");
    }

    [Fact]
    public void 布尔选项不受类型化配置门控()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Strict,
            TestVendors.CreateModel(
                hasTypedConfiguration: true,
                effort: "high",
                options: new() { ["parallel_tool_calls"] = true, ["store"] = false }));

        parameters[ParallelToolCallsPath].GetBoolean().Should().BeTrue();
        parameters["$.store"].GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void 未声明的选项键不会被写进请求()
    {
        var parameters = TestVendors.Resolve(
            TestVendors.Strict,
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                options: new() { ["totally.unknown"] = "x" }));

        parameters.Should().NotContainKey("$.totally.unknown");
    }

    [Fact]
    public void 常量参数忽略来源链()
    {
        var vendor = new LlmVendorDescriptor(
            "const-vendor",
            "Const",
            new Uri("https://const.test/v1/"),
            new Dictionary<string, LlmVendorProtocol>(StringComparer.Ordinal)
            {
                [LlmProtocols.OpenAiChat] = new(
                [
                    new LlmVendorParameter(
                        "$.stream_options.include_usage",
                        LlmVendorValueType.Boolean,
                        ["option:ignored"],
                        Value: System.Text.Json.JsonSerializer.SerializeToElement(true))
                ])
            });

        var parameters = new LlmVendorCatalog()
            .Add(vendor)
            .Let(catalog => new LlmOptionResolver(
                    TestVendors.CreateConnection("const-vendor"),
                    TestVendors.CreateModel(hasTypedConfiguration: false))
                .Resolve(catalog.GetRequired("const-vendor").ProtocolOrNull(LlmProtocols.OpenAiChat)!.Parameters));

        parameters["$.stream_options.include_usage"].GetBoolean().Should().BeTrue();
    }
}

file static class CatalogExtensions
{
    internal static TResult Let<TSource, TResult>(this TSource source, Func<TSource, TResult> projection) =>
        projection(source);
}

public sealed class LlmImageToolCapabilityTests
{
    [Fact]
    public void 未声明多模态时不受理工具结果图片()
    {
        var capability = LlmImageToolCapability.Resolve(
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                limits: new LlmModelLimits(IsMultimodal: false)));

        capability.Delivery.Should().Be(LlmImageToolDelivery.Unsupported);
        capability.UnavailableReason.Should().NotBeNull();
    }

    [Theory]
    [InlineData(LlmProtocols.AnthropicMessages, LlmImageToolDelivery.Native)]
    [InlineData(LlmProtocols.OpenAiResponses, LlmImageToolDelivery.Native)]
    [InlineData(LlmProtocols.OpenAiChat, LlmImageToolDelivery.FollowUpUserMessage)]
    public void 协议决定图片投递形状(string protocol, LlmImageToolDelivery expected)
    {
        var capability = LlmImageToolCapability.Resolve(
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                protocol: protocol,
                limits: new LlmModelLimits(IsMultimodal: true)));

        capability.Delivery.Should().Be(expected);
    }

    [Fact]
    public void 未验证的协议保持不支持()
    {
        LlmImageToolCapability.Resolve(
                TestVendors.CreateModel(
                    hasTypedConfiguration: false,
                    protocol: LlmProtocols.OpenAiImages,
                    limits: new LlmModelLimits(IsMultimodal: true)))
            .Delivery.Should().Be(LlmImageToolDelivery.Unsupported);
    }
}
