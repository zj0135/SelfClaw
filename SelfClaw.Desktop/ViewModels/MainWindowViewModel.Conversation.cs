using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task SetConversationModeCoreAsync(ConversationMode nextMode)
    {
        var previousConversation = SelectedConversation;
        var previousConversationHasContent = _messages.Count > 0 || _toolRuns.Count > 0;

        if (SelectedConversationMode == nextMode && previousConversation?.Mode == nextMode)
        {
            return;
        }

        SelectedConversationMode = nextMode;
        ClearPlanPanelState(publishShell: false);
        RefreshPlanningModeForSelection(publishShell: false);

        if (IsBusy)
        {
            return;
        }

        var existingConversation = GetFilteredConversations().FirstOrDefault();
        if (existingConversation is not null)
        {
            ApplyConversationFilter(existingConversation.Id);
            return;
        }

        if (nextMode == ConversationMode.Channel)
        {
            ApplyConversationFilter();
            StatusText = "Channel conversations will appear automatically after external messages arrive.";
            return;
        }

        if (previousConversation is null)
        {
            await CreateNewConversationAsync();
            return;
        }

        if (!previousConversationHasContent)
        {
            var updated = previousConversation with
            {
                Mode = nextMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(updated);
            return;
        }

        await CreateNewConversationAsync();
    }

    private static ConversationMode ParseConversationMode(string? modeId)
        => modeId?.Trim().ToLowerInvariant() switch
        {
            "channel" => ConversationMode.Channel,
            _ => ConversationMode.Programming
        };

    private static string ConversationModeToId(ConversationMode mode)
        => mode switch
        {
            ConversationMode.Channel => "channel",
            _ => "programming"
        };
}
