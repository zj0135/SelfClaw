namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// A turn-level notice produced by the host (for example a Plugin hook failure the turn ignored).
/// It is recorded as a <see cref="SelfClaw.Core.Models.MessageSegmentKind.Notice"/> segment and is
/// never replayed to the model.
/// </summary>
public sealed record RunNoticeEvent(string Text) : AgentStreamEvent;
