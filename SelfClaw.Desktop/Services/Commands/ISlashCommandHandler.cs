namespace SelfClaw.Desktop.Services;

public interface ISlashCommandHandler
{
    SlashCommandDefinition Definition { get; }

    Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context);
}
