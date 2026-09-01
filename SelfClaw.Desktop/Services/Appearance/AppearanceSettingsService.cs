using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Desktop.Services.Appearance.Models;

namespace SelfClaw.Desktop.Services.Appearance;

/// <summary>
/// 外观偏好的读写。真值在 desktop-settings.json 的 appearance 节点；前端的
/// localStorage 只是让首屏能同步拿到主题的缓存，两者不一致时以这里为准。
/// </summary>
public sealed class AppearanceSettingsService
{
    private const string SettingsNodeName = "appearance";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] Modes = ["light", "dark", "system"];

    // 与前端 FONT_SCALE_STEPS 对应。放开成任意值会让持久化的设置把界面弄坏，
    // 而用户很难自己改回来。
    private static readonly double[] Scales = [0.9d, 1d, 1.1d, 1.25d];

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppearanceSettings? _cached;

    public AppearanceSettingsService(DesktopSettingsJsonStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        _settingsStore = settingsStore;
    }

    public async Task<AppearanceSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached ??= Normalize(
                await _settingsStore
                    .ReadNodeAsync<AppearanceSettings>(SettingsNodeName, JsonOptions, cancellationToken)
                    .ConfigureAwait(false));
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppearanceSettings> SaveAsync(
        AppearanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = normalized;
            await _settingsStore
                .WriteNodeAsync(SettingsNodeName, normalized, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 窗口在 OnSourceInitialized 里同步取用：那时 WebView 还没加载，拿不到前端推送，
    /// 只能读上一次缓存下来的解析结果。刻意不做 I/O —— 那是 UI 线程，阻塞读文件会
    /// 拖慢启动。App.OnStartup 已在显示窗口前 await 过 <see cref="GetAsync"/>，
    /// 缓存那时就已就绪；真的没有（首次启动无文件）就回落浅色，与 tokens.css 的默认一致。
    /// </summary>
    public bool CachedIsDark => _cached?.IsDark ?? false;

    private static AppearanceSettings Normalize(AppearanceSettings? settings)
    {
        if (settings is null)
        {
            return AppearanceSettings.Default;
        }

        var mode = Modes.Contains(settings.Mode, StringComparer.OrdinalIgnoreCase)
            ? settings.Mode.ToLowerInvariant()
            : AppearanceSettings.Default.Mode;

        // 显式选了明暗就以它为准；只有 system 才需要前端缓存的解析值。
        var resolved = mode switch
        {
            "light" or "dark" => mode,
            _ => settings.IsDark ? "dark" : "light"
        };

        return settings with
        {
            Mode = mode,
            ResolvedTheme = resolved,
            UiFontFamily = Trim(settings.UiFontFamily),
            UiFontScale = NearestScale(settings.UiFontScale),
            TextColor = Trim(settings.TextColor),
            CodeFontFamily = Trim(settings.CodeFontFamily),
            CodeFontScale = NearestScale(settings.CodeFontScale),
            CodeSurface = Trim(settings.CodeSurface),
            CodeInk = Trim(settings.CodeInk),
        };
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static double NearestScale(double value)
        => Scales.Contains(value) ? value : 1d;
}
