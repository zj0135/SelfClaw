namespace SelfClaw.Infrastructure.Git;

internal sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string Message => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput.Trim()
        : StandardError.Trim();
}
