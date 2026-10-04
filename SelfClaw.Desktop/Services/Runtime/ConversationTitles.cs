namespace SelfClaw.Desktop.Services.Runtime;

internal static class ConversationTitles
{
    internal const string NewConversation = "New chat";
    private const int MaximumLength = 48;

    internal static string FromPrompt(string prompt)
    {
        var title = prompt.ReplaceLineEndings(" ").Trim();
        return title.Length > MaximumLength ? title[..MaximumLength] + "..." : title;
    }
}
