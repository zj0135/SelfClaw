using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityPanelWireState(
    Guid SubscriptionId,
    Guid? ParentConversationId,
    long Revision,
    IReadOnlyList<ActivityPanelWireSection> Sections,
    string? RequestId = null,
    string? StateError = null,
    int SchemaVersion = 1,
    string Type = "activity-panel/state",
    [property: JsonIgnore] long CaptureSequence = 0);
