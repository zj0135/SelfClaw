namespace SelfClaw.Desktop.Services.WebView;

internal sealed record WebViewHostCommand(
    WebViewHostCommandKind Kind,
    string? Value = null);
