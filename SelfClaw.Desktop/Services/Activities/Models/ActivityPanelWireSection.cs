using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityPanelWireSection(
    SubagentActivityCounts Counts,
    string ListVersion,
    string? Cursor,
    string? NextCursor,
    bool ListReset,
    IReadOnlyList<ActivityPanelWireTask> Tasks,
    Guid? DetailSelectionId,
    ActivityPanelWireDetail? Detail,
    string? DetailError = null,
    string Id = "subagents",
    string Kind = "subagents",
    string Title = "子代理",
    ActivityPanelWireTask? SelectedTask = null);
