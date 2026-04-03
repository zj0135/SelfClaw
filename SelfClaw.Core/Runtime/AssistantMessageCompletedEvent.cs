using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record AssistantMessageCompletedEvent(MessageRecord Message) : ChatRuntimeEvent;
