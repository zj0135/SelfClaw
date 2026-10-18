using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// A5: the summarizer needs the meaning of an image, never the pixels. The planner already declines
/// image-bearing units; this covers the second line of defence inside the compressor.
/// </summary>
public sealed class DirectContextCompressorAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public async Task Replaces_images_with_a_text_placeholder_before_summarizing()
    {
        var client = new RecordingChatClient();
        var units = new[]
        {
            new DirectPromptHistoryUnit(
                [
                    new ChatMessage(ChatRole.User,
                        [new DataContent(PngBytes, "image/png") { Name = "photo.png" }, new TextContent("what is this?")])
                ], 1),
            new DirectPromptHistoryUnit([new ChatMessage(ChatRole.User, "plain prompt")], 2)
        };

        var outcome = await CreateCompressor(client).TryCompressAsync(new DirectCompressionRequest(
            units, null, 1200, null, CreateLease(client, "turn-model"), Guid.NewGuid(), Guid.NewGuid()));

        var sent = client.Requests.Single();
        sent.SelectMany(message => message.Contents).OfType<DataContent>().Should().BeEmpty();
        var imageMessage = sent.Should().ContainSingle(message =>
            message.Contents.OfType<TextContent>().Any(text => text.Text == "what is this?")).Subject;
        imageMessage.Contents.OfType<TextContent>().Select(text => text.Text)
            .Should().Equal("[image: photo.png]", "what is this?");
        outcome.Should().NotBeNull();
    }

    private static DirectContextCompressor CreateCompressor(RecordingChatClient client)
        => new(new DirectAgentChatRuntimeTests.FakeChatClientFactory(client), new StubAiModelCatalog(),
            new DirectTokenCounter());

    private static AiChatClientLease CreateLease(IChatClient client, string model)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfile(Guid.NewGuid(), Guid.NewGuid(), "test-model",
            AiProviderApiFormat.OpenAIChatCompletions, model, new AiSamplingOptions(false, 0d, false, 0d),
            new Dictionary<string, System.Text.Json.JsonElement>(), now, now);
        return new AiChatClientLease(client, new ChatOptions(), profile, new HttpClient());
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("summarized history")])
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
