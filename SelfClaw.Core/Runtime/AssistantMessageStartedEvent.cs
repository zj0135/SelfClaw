using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record AssistantMessageStartedEvent(MessageRecord Message) : ChatRuntimeEvent;
