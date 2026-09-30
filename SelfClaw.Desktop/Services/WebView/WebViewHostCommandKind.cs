namespace SelfClaw.Desktop.Services.WebView;

internal enum WebViewHostCommandKind
{
    OpenLink,

    /// <summary>
    /// 在资源管理器里打开一个工作区根。Value 是宿主按 id 校验过的绝对路径，
    /// 前端不能直接指定路径。
    /// </summary>
    OpenInExplorer,
    StartWindowDrag,
    StartWindowResize,
    MinimizeWindow,
    ToggleMaximizeWindow,
    CloseWindow,
    ToggleTerminal,

    /// <summary>
    /// 把原生标题栏切成深色或浅色（Value 为 "dark" / "light"）。「跟随系统」只有前端
    /// 能解，所以明暗结论由它算出后经这条命令交回窗口。
    /// </summary>
    ApplyCaptionTheme
}
