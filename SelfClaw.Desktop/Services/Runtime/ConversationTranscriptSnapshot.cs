using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record ConversationTranscriptSnapshot(
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolRuns);
