using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class ModelOptionReaderTests
{
    // 模型列表刷新会把 7 个 display.* 目录元数据写进每个档案，而它们永不上网：报成「未知选项」既
    // 事实错误，又会把真正拼错的线参数淹掉（2026-11-09 日志里 252 行全是它们）。
    [Fact]
    public void Skips_the_catalog_metadata_that_every_profile_carries()
    {
        var logger = new CapturingLogger();
        var reader = CreateReader(logger, """
            {"display.name":"Deepseek-V4.1-Flash","display.contextLength":1000000,
             "display.maxOutputTokens":393216,"display.priceInPerMTok":0.15}
            """);

        reader.LogUnknown(RecognizedKeys);

        logger.Messages.Should().BeEmpty("catalog metadata never travels on the wire, so it cannot be unknown");
    }

    [Theory]
    [InlineData("context_window_tokens")]
    [InlineData("tokenizer.encoding")]
    public void Skips_the_host_keys_that_shape_the_direct_budget(string key)
    {
        var logger = new CapturingLogger();

        CreateReader(logger, $$"""{"{{key}}":123}""").LogUnknown(RecognizedKeys);

        logger.Messages.Should().BeEmpty();
    }

    // 真正的信号必须留着：线参数拼错会静默丢掉思考等级或输出上限，没有第二条路径能发现它。
    [Fact]
    public void Still_reports_a_misspelled_wire_option()
    {
        var logger = new CapturingLogger();
        var reader = CreateReader(logger, """{"reasoning_efort":"high","thinking.type":"enabled"}""");

        reader.LogUnknown(RecognizedKeys);

        var message = logger.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("reasoning_efort").And.Contain("Deepseek-V4.1-Flash");
    }

    private static readonly IReadOnlySet<string> RecognizedKeys =
        new HashSet<string>(StringComparer.Ordinal) { "thinking.type" };

    private static ModelOptionReader CreateReader(CapturingLogger logger, string optionsJson)
        => new(logger, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson) ?? [], "Deepseek-V4.1-Flash");

    private sealed class CapturingLogger : ILogger
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
