using SelfClaw.Core.Interfaces;

namespace SelfClaw.Desktop.Services;

public sealed class InitSlashCommandHandler : ISlashCommandHandler
{
    private readonly IWorkspaceMemoryInitializationService _initializer;

    public InitSlashCommandHandler(IWorkspaceMemoryInitializationService initializer)
    {
        _initializer = initializer;
    }

    public SlashCommandDefinition Definition { get; } = new(
        "init",
        "/init",
        "Init",
        "Analyze current workspace and generate AGENTS.md.",
        null,
        RequiresConfirmation: true);

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context)
    {
        if (context.WorkspaceRoot is null)
        {
            return SlashCommandResult.Error("Select a workspace before running /init.");
        }

        if (context.Profile is null || string.IsNullOrWhiteSpace(context.ApiKey))
        {
            return SlashCommandResult.Error("Select a model profile before running /init.");
        }

        if (!context.Confirmed && _initializer.AgentsFileExists(context.WorkspaceRoot))
        {
            return SlashCommandResult.Confirmation("AGENTS.md already exists. Confirm /init to overwrite it.");
        }

        var result = await _initializer.InitializeAsync(
            context.WorkspaceRoot,
            context.Profile,
            context.ApiKey,
            context.CancellationToken);

        return SlashCommandResult.Success(
            result.OverwroteExisting
                ? "AGENTS.md updated."
                : "AGENTS.md created.");
    }
}
