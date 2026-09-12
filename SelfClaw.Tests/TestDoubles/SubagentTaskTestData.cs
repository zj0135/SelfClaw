using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;

namespace SelfClaw.Tests.TestDoubles;

internal static class SubagentTaskTestData
{
    internal static async Task<SubagentTaskRecord> CreateRunningTaskAsync(
        SqliteConversationRepository conversations,
        SqliteSubagentTaskRepository tasks)
    {
        await CreateQueuedTaskAsync(conversations, tasks);
        return await tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("The fixture task could not be claimed.");
    }

    internal static async Task<SubagentTaskRecord> CreateQueuedTaskAsync(
        SqliteConversationRepository conversations,
        SqliteSubagentTaskRepository tasks,
        ConversationRecord? parent = null)
    {
        var now = DateTimeOffset.UtcNow;
        parent ??= new ConversationRecord(
            Guid.NewGuid(), "Parent", null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, "build", now, now);
        await conversations.UpsertConversationAsync(parent);
        var childId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();
        var child = new ConversationRecord(
            childId, "Subagent: Reviewer", null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, "reviewer", now, now,
            Kind: ConversationKind.Subagent, ParentConversationId: parent.Id);
        const string taskText = "Inspect the current implementation.";
        var message = new MessageRecord(
            Guid.NewGuid(), childId, MessageRole.User, taskText, MessageStatus.Completed, now, now);
        var serializer = new SubagentTaskSnapshotSerializer();
        var definition = new SubagentDefinitionSnapshot(
            1, "reviewer", "Reviewer", "Reviews code", null, "read-only", [], [], [], 900,
            "Review only the supplied task.");
        var parentAgent = new AgentRuntimeDefinition(
            "build", "Build", string.Empty, AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy, [], [], [], ["reviewer"], "Parent instructions");
        var parentSnapshot = new SubagentParentExecutionSnapshot(
            1, parentAgent, modelProfileId, null, ToolPermissionMode.RequireApproval,
            new DirectCapabilityCeiling(AgentRuntimeDefinition.SystemToolPolicy, [], [], [], ["reviewer"]));
        var task = new SubagentTaskRecord(
            Guid.NewGuid(), parent.Id, Guid.NewGuid(), childId, Guid.NewGuid(), "reviewer", "Reviewer",
            taskText, SubagentTaskStatus.Queued, 1, null,
            serializer.Serialize(definition), serializer.Serialize(parentSnapshot), modelProfileId, 900,
            null, null, null, null, null, null, now, null, null, now, now);
        await tasks.CreateAsync(new SubagentTaskCreation(child, message, task));
        return task;
    }
}
