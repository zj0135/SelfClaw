using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record ToolExecutionStartedEvent(ToolExecutionRecord Record) : ChatRuntimeEvent;