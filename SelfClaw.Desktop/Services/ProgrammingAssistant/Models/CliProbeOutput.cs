namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

internal sealed record CliProbeOutput(int ExitCode, string StandardOutput, string StandardError, bool Truncated);
