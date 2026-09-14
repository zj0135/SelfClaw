using SelfClaw.Desktop.Services.Transcript.Views;
namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityPanelWireDetail(
    Guid TaskId,
    ActivityPanelWireTask Task,
    string TaskText,
    string ContentVersion,
    string ContentOrigin,
    string HistoryCompleteness,
    int BlockOffset,
    int TotalBlocks,
    int? EarlierOffset,
    int? LaterOffset,
    TranscriptRenderItem? Message,
    IReadOnlyList<TranscriptRenderSegment> UnplacedTools,
    IReadOnlyList<ActivityContentReference> Content);
