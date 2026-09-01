namespace SelfClaw.Desktop.Services.Appearance.Models;

/// <summary>
/// 外观偏好。字段与前端 useAppearance 的 state 一一对应，序列化为 camelCase 后
/// 原样存进 desktop-settings.json 的 appearance 节点。
/// </summary>
/// <param name="Mode">light / dark / system。</param>
/// <param name="ResolvedTheme">
/// Mode 解析后的实际明暗。「跟随系统」只有前端能解（matchMedia），这里缓存它上一次
/// 的结论，供窗口在 WebView 加载完成之前就把原生标题栏设成正确的明暗 —— 否则深色
/// 用户每次启动都会看到标题栏先白一下。
/// </param>
public sealed record AppearanceSettings(
    string Mode = "system",
    string ResolvedTheme = "light",
    string UiFontFamily = "",
    double UiFontScale = 1d,
    string TextColor = "",
    string CodeFontFamily = "",
    double CodeFontScale = 1d,
    string CodeSurface = "",
    string CodeInk = "")
{
    public static AppearanceSettings Default { get; } = new();

    public bool IsDark => string.Equals(ResolvedTheme, "dark", StringComparison.OrdinalIgnoreCase);
}
