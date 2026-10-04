using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用级设置（语言、主题）的持久化。存于 %AppData%\SteamEYA\settings.json，与账号历史同目录。
/// 读写以同步小文件为主，调用方不多（启动读一次、设置页改动时写），故用简单锁而非账号历史那套文件门。
/// </summary>
internal sealed class SettingsService
{
    private const string AppFolderName = "SteamEYA";
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsFilePath;
    private readonly object _gate = new();

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        _settingsFilePath = Path.Combine(AppFolderPath, SettingsFileName);
    }

    /// <summary>数据根目录（%AppData%\SteamEYA），供“打开数据目录”使用。</summary>
    public string AppFolderPath { get; }

    /// <summary>「个性化」头像的固定落盘路径（裁剪后的 512² JPEG）。</summary>
    public string PersonalizationAvatarPath => Path.Combine(AppFolderPath, "personalization", "avatar.jpg");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings is not null)
                    {
                        // JSON 里显式的 "groups": null 会覆盖属性初始值，消费方（LoadGroups 等）直接 .Groups 会 NRE。
                        settings.Groups ??= [];
                        // 同理防护配装预设：显式 null 会让配装页导航、一键配装直接 NRE。
                        settings.Loadout ??= CsLoadoutPreset.Default();
                        settings.Loadout.T ??= new Dictionary<uint, uint>();
                        settings.Loadout.Ct ??= new Dictionary<uint, uint>();
                        // 旧版保存的是系统字体族名；新版只允许 Assets/Fonts 内置字体 URI。
                        if (!string.IsNullOrWhiteSpace(settings.FontFamily) &&
                            !settings.FontFamily.StartsWith("ms-appx:///Assets/Fonts/", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.FontFamily = null;
                        }
                        settings.BackgroundOpacity = Math.Clamp(
                            settings.BackgroundOpacity,
                            AppSettings.MinimumBackgroundOpacity,
                            AppSettings.MaximumBackgroundOpacity);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("读取应用设置失败，按默认设置处理。", ex);
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            // 先写临时文件再原子替换，避免写入中断留下半截 settings.json。
            var tempPath = _settingsFilePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                Directory.CreateDirectory(AppFolderPath);
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                File.WriteAllText(tempPath, json);
                if (File.Exists(_settingsFilePath))
                {
                    File.Replace(tempPath, _settingsFilePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _settingsFilePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("保存应用设置失败。", ex);
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 清理残留临时文件失败无需上报。
                }
            }
        }
    }
}

internal sealed class AppSettings
{
    public const int MinimumBackgroundOpacity = 60;
    public const int MaximumBackgroundOpacity = 80;

    /// <summary>界面语言代码：zh-Hans / en / zh-Hant。null 表示尚未选择（首次启动按系统语言推断）。</summary>
    public string? Language { get; set; }

    /// <summary>主题：Default（跟随系统）/ Light / Dark。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>全局界面字体族名；null/空表示使用 WinUI 系统默认字体。</summary>
    public string? FontFamily { get; set; }

    /// <summary>是否使用 Desktop Acrylic 毛玻璃；关闭时使用普通 Mica 背景。</summary>
    public bool GlassEffectEnabled { get; set; }

    /// <summary>毛玻璃模式下卡片和控件表面的不透明度百分比（60-80）。</summary>
    public int BackgroundOpacity { get; set; } = 72;

    /// <summary>
    /// 已解析并持久化的 Steam 安装目录（含 steam.exe 的根目录）。首次启动自动检测后写入，之后直接复用，
    /// 不必每次上号重新检测。null/空表示尚未解析；失效（目录里找不到 steam.exe）时会重新自动检测，
    /// 仍找不到则弹框让用户手动选择。详见 <see cref="SteamPathCoordinator"/>。
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>唯一的 CS2 配装预设，供装备页面编辑与登录页一键装配。新用户用项目内置默认配装。</summary>
    public CsLoadoutPreset Loadout { get; set; } = CsLoadoutPreset.Default();

    /// <summary>「个性化」面板里设置的昵称，供登录页一键把账号资料设为该值。null/空表示不改昵称。</summary>
    public string? PersonaName { get; set; }

    /// <summary>「个性化」面板里设置的真实姓名（资料页 real_name 字段）。null/空表示不改。</summary>
    public string? ProfileRealName { get; set; }

    /// <summary>「个性化」面板里设置的概要（资料页 summary 字段，可多行）。null/空表示不改。</summary>
    public string? ProfileSummary { get; set; }

    /// <summary>是否在「一键个性化」完成后清空账号的曾用名记录。</summary>
    public bool ClearAliasHistoryOnPersonalize { get; set; }

    /// <summary>用户自定义账号分组定义（名称/排序）。成员关系存于各账号的 GroupIds，此处只存定义。</summary>
    public List<AccountGroup> Groups { get; set; } = [];

    /// <summary>是否在登录时把「来源账号」的 CS2 本地设置复制到要登录的账号。</summary>
    public bool Cs2SyncOnLogin { get; set; }

    /// <summary>CS2 设置同步的「来源账号」SteamID64；空表示未选择。详见 <see cref="Cs2CloudService"/>。</summary>
    public string? Cs2SyncSourceSteamId { get; set; }

    /// <summary>更新检查使用的 GitHub 站点代码（direct / gh-proxy.org / v4.gh-proxy.org / v6.gh-proxy.org / cdn.gh-proxy.org），与 GitHubUpdateService.ProxySites 保持一致；未知值回退 direct。</summary>
    public string UpdateProxySite { get; set; } = "direct";
}

// 与账号历史一致用 source generator：JsonSerializerDefaults.Web（camelCase、大小写不敏感），AOT 下可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CsLoadoutPreset))]
[JsonSerializable(typeof(AccountGroup))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
