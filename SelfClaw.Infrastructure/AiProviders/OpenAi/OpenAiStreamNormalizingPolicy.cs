using System.ClientModel.Primitives;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// OpenAI-compatible providers stream fields the OpenAI SDK and Microsoft.Extensions.AI either
/// reject or ignore. An empty or missing <c>tool_calls[].type</c> is rejected while deserializing
/// the SSE frame (<c>Unknown ChatToolCallKind value</c>), and reasoning text is surfaced as
/// <c>reasoning</c> / <c>reasoning_text</c> / <c>reasoning_details</c> where M.E.AI only reads
/// <c>reasoning_content</c>, which silently drops every thinking delta. This policy normalizes the
/// response body as it streams past. See <see cref="OpenAiStreamNormalizingStream"/>.
/// </summary>
internal sealed class OpenAiStreamNormalizingPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        => ProcessAsync(message, pipeline, currentIndex).AsTask().GetAwaiter().GetResult();

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        var response = message.Response;
        if (response?.ContentStream is not { } contentStream)
        {
            return;
        }

        var contentType = response.Headers.TryGetValue("Content-Type", out var header) ? header : null;
        if (contentType is null ||
            !contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        response.ContentStream = new OpenAiStreamNormalizingStream(contentStream);
    }
}
