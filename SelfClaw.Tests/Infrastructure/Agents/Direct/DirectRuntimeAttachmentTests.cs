using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// A5: the runtime is the seam where a stored attachment becomes part of the provider request. The bytes
/// must reach the provider, and an attachment that vanished from disk must only cost that image.
/// </summary>
public sealed class DirectRuntimeAttachmentTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    private readonly string _rootPath;
    private readonly StoragePaths _storagePaths;
    private readonly AttachmentStore _store;
    private readonly AttachmentContentReader _reader;

    public DirectRuntimeAttachmentTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storagePaths = StoragePathDefaults.Create(_rootPath, Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        _store = new AttachmentStore(_storagePaths);
        _reader = new AttachmentContentReader(_storagePaths);
    }

    [Fact]
    public async Task A_turn_sends_the_stored_image_ahead_of_the_prompt_text()
    {
        var client = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client);
        var attachment = await _store.IngestAsync(Guid.NewGuid(), new MemoryStream(PngBytes), "photo.png", "image/png");
        var request = RequestWithAttachment(factory.Profile.Id, attachment);

        var events = await CollectAsync(DirectAgentChatRuntimeTests
            .CreateRuntime(factory, attachmentContentReader: _reader)
            .StreamTurnAsync(request));

        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
        var userMessage = client.LastMessages.Last(message => message.Role == ChatRole.User);
        userMessage.Contents.Should().HaveCount(2);
        userMessage.Contents[0].Should().BeOfType<DataContent>()
            .Which.Data.ToArray().Should().Equal(PngBytes);
        userMessage.Contents[1].Should().BeOfType<TextContent>().Which.Text.Should().Be("user prompt");
    }

    [Fact]
    public async Task A_turn_completes_when_the_attachment_file_is_gone()
    {
        var client = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client);
        var attachment = await _store.IngestAsync(Guid.NewGuid(), new MemoryStream(PngBytes), "photo.png", "image/png");
        File.Delete(attachment.StoragePath);
        var request = RequestWithAttachment(factory.Profile.Id, attachment);

        var events = await CollectAsync(DirectAgentChatRuntimeTests
            .CreateRuntime(factory, attachmentContentReader: _reader)
            .StreamTurnAsync(request));

        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
        client.LastMessages.SelectMany(message => message.Contents).OfType<DataContent>().Should().BeEmpty();
        client.LastMessages.Last(message => message.Role == ChatRole.User).Text.Should().Be("user prompt");
    }

    private static DirectChatTurnRequest RequestWithAttachment(Guid modelProfileId, MessageAttachmentRecord attachment)
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(modelProfileId);
        var messages = request.Messages.ToList();
        var lastUser = messages.FindLastIndex(message => message.Role == MessageRole.User);
        messages[lastUser] = messages[lastUser] with { Attachments = [attachment] };
        return request with { Messages = messages };
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> LastMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages.Clear();
            LastMessages.AddRange(messages);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer")])
            {
                MessageId = "answer-message",
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
