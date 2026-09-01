using System.Text.Json;
using SelfClaw.Desktop.Services.Appearance.Models;

namespace SelfClaw.Desktop.Services.Appearance;

internal sealed class AppearanceSettingsBridge
{
    private readonly AppearanceSettingsService _settingsService;

    public AppearanceSettingsBridge(AppearanceSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    /// <summary>
    /// 返回值里的 <c>Response</c> 走 WebView 回包，<c>IsDark</c> 供路由转成
    /// 原生标题栏命令 —— 保存外观既要落盘，也要让 WPF 那一侧跟着换明暗。
    /// </summary>
    public async Task<(object Response, bool? IsDark)?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("appearance/get-state" or "appearance/save"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var settings = type == "appearance/save"
                ? await _settingsService.SaveAsync(ReadSettings(payload), cancellationToken)
                : await _settingsService.GetAsync(cancellationToken);

            return (BuildResponse(requestId, settings), settings.IsDark);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                new
                {
                    type = "appearance/state",
                    requestId,
                    settings = AppearanceSettings.Default,
                    error = exception.Message
                },
                null);
        }
    }

    // 前端把 settings 放在嵌套对象里，resolvedTheme 与它并列（解析结果不属于用户设置，
    // 是前端 matchMedia 的结论）。这里把两者合成一条记录落盘。
    private static AppearanceSettings ReadSettings(JsonElement payload)
    {
        var node = payload.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
            ? settings
            : payload;

        return new AppearanceSettings(
            Mode: ReadOptionalString(node, "mode") ?? AppearanceSettings.Default.Mode,
            ResolvedTheme: ReadOptionalString(payload, "resolvedTheme")
                           ?? ReadOptionalString(node, "resolvedTheme")
                           ?? AppearanceSettings.Default.ResolvedTheme,
            UiFontFamily: ReadOptionalString(node, "uiFontFamily") ?? string.Empty,
            UiFontScale: ReadDouble(node, "uiFontScale"),
            TextColor: ReadOptionalString(node, "textColor") ?? string.Empty,
            CodeFontFamily: ReadOptionalString(node, "codeFontFamily") ?? string.Empty,
            CodeFontScale: ReadDouble(node, "codeFontScale"),
            CodeSurface: ReadOptionalString(node, "codeSurface") ?? string.Empty,
            CodeInk: ReadOptionalString(node, "codeInk") ?? string.Empty);
    }

    private static object BuildResponse(string? requestId, AppearanceSettings settings)
        => new
        {
            type = "appearance/state",
            requestId,
            settings = new
            {
                mode = settings.Mode,
                resolvedTheme = settings.ResolvedTheme,
                uiFontFamily = settings.UiFontFamily,
                uiFontScale = settings.UiFontScale,
                textColor = settings.TextColor,
                codeFontFamily = settings.CodeFontFamily,
                codeFontScale = settings.CodeFontScale,
                codeSurface = settings.CodeSurface,
                codeInk = settings.CodeInk
            }
        };

    private static double ReadDouble(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : 1d;

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
