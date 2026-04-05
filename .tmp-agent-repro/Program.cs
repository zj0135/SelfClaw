using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Repositories;

var root = Path.Combine(Path.GetTempPath(), "SelfClawRepoRepro_" + Guid.NewGuid().ToString("N"));
var storage = new StoragePaths(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets"));
var database = new SqliteDatabase(storage);
var profiles = new SqliteProfileRepository(database);
var conversations = new SqliteConversationRepository(database);
await profiles.InitializeAsync();
await conversations.InitializeAsync();

var now = DateTimeOffset.UtcNow;
var profile = new ProviderProfile(Guid.NewGuid(), "Local", "https://api.example.com/v1", "gpt-4.1", ApiStyle.OpenAICompatible, "secret:test", now, now);
await profiles.UpsertProfileAsync(profile);

var rootConversation = new ConversationRecord(Guid.NewGuid(), "Root", profile.Id, null, ConversationMode.Team, ToolPermissionMode.RequireApproval, 3, TeamOutputMode.AutoDocument, now, now);
var persistedRoot = await conversations.UpsertConversationAsync(rootConversation);
var agentId = Guid.NewGuid();

var first = new ConversationRecord(Guid.NewGuid(), "First", profile.Id, null, ConversationMode.Team, ToolPermissionMode.RequireApproval, 3, TeamOutputMode.AutoDocument, now.AddMinutes(1), now.AddMinutes(1), persistedRoot.Id, persistedRoot.Id, agentId, "Product Manager", "Product Manager");
var persistedFirst = await conversations.UpsertConversationAsync(first);
await conversations.UpsertMessageAsync(new MessageRecord(Guid.NewGuid(), persistedFirst.Id, MessageRole.User, "hello", MessageStatus.Completed, now, now));

var second = new ConversationRecord(Guid.NewGuid(), "Second", profile.Id, null, ConversationMode.Team, ToolPermissionMode.RequireApproval, 3, TeamOutputMode.AutoDocument, now.AddMinutes(2), now.AddMinutes(2), persistedRoot.Id, persistedRoot.Id, agentId, "Product Manager", "Product Manager");
var persistedSecond = await conversations.UpsertConversationAsync(second);
var loadedConversations = await conversations.ListConversationsAsync();
var loadedMessages = await conversations.ListMessagesAsync(persistedSecond.Id);

Console.WriteLine($"FIRST={persistedFirst.Id}");
Console.WriteLine($"SECOND={persistedSecond.Id}");
Console.WriteLine($"CONVERSATION_COUNT={loadedConversations.Count}");
foreach (var conversation in loadedConversations.OrderBy(item => item.CreatedAtUtc))
{
    Console.WriteLine($"CONV {conversation.Id} title={conversation.Title} parent={conversation.ParentConversationId} root={conversation.RootConversationId} agent={conversation.BoundAgentId}");
}
Console.WriteLine($"MESSAGE_COUNT={loadedMessages.Count}");
foreach (var message in loadedMessages)
{
    Console.WriteLine($"MSG {message.Id} conv={message.ConversationId} content={message.MarkdownContent}");
}

try { Directory.Delete(root, true); } catch { }
