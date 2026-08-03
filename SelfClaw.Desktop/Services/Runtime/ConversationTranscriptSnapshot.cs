using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record ConversationTranscriptSnapshot(
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolRuns,
    IReadOnlyDictionary<Guid, ToolRunAnchor> ToolRunAnchors);
