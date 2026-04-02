using Markdig;

namespace SelfClaw.Infrastructure.Tools;

public sealed class MarkdownHtmlRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public string ToHtml(string markdown)
        => Markdown.ToHtml(markdown ?? string.Empty, _pipeline);
}