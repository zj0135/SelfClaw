namespace SelfClaw.Desktop.Services.WebView;

internal enum WebViewHostCommandKind
{
    OpenLink,
    StartWindowDrag,
    StartWindowResize,
    MinimizeWindow,
    ToggleMaximizeWindow,
    CloseWindow,
    ToggleTerminal,
    SettingsClosed,

    /// <summary>
    /// 把原生标题栏切成深色或浅色（Value 为 "dark" / "light"）。「跟随系统」只有前端
    /// 能解，所以明暗结论由它算出后经这条命令交回窗口。
    /// </summary>
    ApplyCaptionTheme
}
