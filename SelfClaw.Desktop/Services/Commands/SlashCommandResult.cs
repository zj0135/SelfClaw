namespace SelfClaw.Desktop.Services;

public sealed record SlashCommandResult(
    bool Succeeded,
    string Message,
    bool RequiresConfirmation = false,
    string? Level = null)
{
    public static SlashCommandResult Success(string message)
        => new(true, message, Level: "success");

    public static SlashCommandResult Error(string message)
        => new(false, message, Level: "error");

    public static SlashCommandResult Confirmation(string message)
        => new(false, message, true, "warning");
}
