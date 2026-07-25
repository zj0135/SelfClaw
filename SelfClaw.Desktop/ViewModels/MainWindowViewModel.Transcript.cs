using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static string BuildAvatarText(MessageRecord message)
    {
        if (message.Role == MessageRole.User)
        {
            return "You";
        }

        if (!string.IsNullOrWhiteSpace(message.AgentName))
        {
            var tokens = message.AgentName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2)
            {
                return string.Concat(tokens[0][0], tokens[1][0]).ToUpperInvariant();
            }

            return message.AgentName.Length <= 2
                ? message.AgentName.ToUpperInvariant()
                : message.AgentName[..2].ToUpperInvariant();
        }

        return message.Role == MessageRole.Assistant ? "SC" : "SYS";
    }
}
