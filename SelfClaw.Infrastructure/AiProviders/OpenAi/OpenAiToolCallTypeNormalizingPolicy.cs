using System.ClientModel.Primitives;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// Some OpenAI-compatible providers stream tool-call deltas with an empty or missing
/// <c>type</c> field. The OpenAI SDK rejects that while deserializing the SSE frame
/// (<c>Unknown ChatToolCallKind value</c>), which aborts the whole turn before
/// Microsoft.Extensions.AI sees the update. This policy rewrites those deltas to
/// <c>"type":"function"</c> as the response body streams past.
/// </summary>
internal sealed class OpenAiToolCallTypeNormalizingPolicy : PipelinePolicy
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

        response.ContentStream = new OpenAiToolCallTypeNormalizingStream(contentStream);
    }
}
