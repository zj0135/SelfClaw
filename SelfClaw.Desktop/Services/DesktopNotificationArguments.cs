using System.Text;

namespace SelfClaw.Desktop.Services;

internal static class DesktopNotificationArguments
{
    public const string ActionKey = "action";
    public const string ConversationIdKey = "conversationId";
    public const string ToolExecutionIdKey = "toolExecutionId";

    public const string OpenAppAction = "openApp";
    public const string OpenConversationAction = "openConversation";
    public const string ApproveToolAction = "approveTool";
    public const string RejectToolAction = "rejectTool";

    public static string Build(params (string Key, string Value)[] arguments)
    {
        var builder = new StringBuilder();

        foreach (var (key, value) in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    public static IReadOnlyDictionary<string, string> Parse(string? arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return values;
        }

        foreach (var segment in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                values[Uri.UnescapeDataString(segment)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..separatorIndex]);
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..]);
            values[key] = value;
        }

        return values;
    }
}
