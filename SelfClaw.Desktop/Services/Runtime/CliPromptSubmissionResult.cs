using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record CliPromptSubmissionResult(
    bool Accepted,
    string? Error,
    ConversationRecord? Conversation,
    WorkspaceRoot? ProvisionedRoot);
