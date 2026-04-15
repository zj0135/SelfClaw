using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record ToolExecutionCompletedEvent(ToolExecutionRecord Record) : ChatRuntimeEvent;