using SelfClaw.Core.Interfaces;

namespace SelfClaw.Desktop.Services;

public sealed class CompactSlashCommandHandler : ISlashCommandHandler
{
    private readonly IConversationContextCompactionService _compactionService;

    public CompactSlashCommandHandler(IConversationContextCompactionService compactionService)
    {
        _compactionService = compactionService;
    }

    public SlashCommandDefinition Definition { get; } = new(
        "compact",
        "/compact",
        "Compact",
        "Compact current conversation history.",
        "[focus]");

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context)
    {
        if (context.Conversation is null)
        {
            return SlashCommandResult.Error("No selected conversation to compact.");
        }

        if (context.Profile is null || string.IsNullOrWhiteSpace(context.ApiKey))
        {
            return SlashCommandResult.Error("Select a model profile before compacting conversation history.");
        }

        if (context.Messages.Count == 0)
        {
            return SlashCommandResult.Error("This conversation has no history to compact.");
        }

        var summary = await _compactionService.CompactNowAsync(
            context.Conversation.Id,
            context.Profile,
            context.ApiKey,
            context.Messages,
            context.Settings.ModelContextWindow,
            string.IsNullOrWhiteSpace(context.Arguments) ? null : context.Arguments.Trim(),
            context.CancellationToken);

        return SlashCommandResult.Success(
            summary is null
                ? "No compactable conversation history was found."
                : "Conversation history compacted.");
    }
}
