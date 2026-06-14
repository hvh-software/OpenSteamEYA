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
            try
            {
                Directory.CreateDirectory(AppFolderPath);
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                // 先写临时文件再原子替换，避免写入中断留下半截 settings.json。
                var tempPath = _settingsFilePath + "." + Path.GetRandomFileName() + ".tmp";
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
            }
        }
    }
}

internal sealed class AppSettings
{
    /// <summary>界面语言代码：zh-Hans / en / zh-Hant。null 表示尚未选择（首次启动按系统语言推断）。</summary>
    public string? Language { get; set; }

    /// <summary>主题：Default（跟随系统）/ Light / Dark。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// 已解析并持久化的 Steam 安装目录（含 steam.exe 的根目录）。首次启动自动检测后写入，之后直接复用，
    /// 不必每次上号重新检测。null/空表示尚未解析；失效（目录里找不到 steam.exe）时会重新自动检测，
    /// 仍找不到则弹框让用户手动选择。详见 <see cref="SteamPathCoordinator"/>。
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>唯一的 CS2 配装预设，供装备页面编辑与登录页一键装配。新用户用项目内置默认配装。</summary>
    public CsLoadoutPreset Loadout { get; set; } = CsLoadoutPreset.Default();
}

// 与账号历史一致用 source generator：JsonSerializerDefaults.Web（camelCase、大小写不敏感），AOT 下可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CsLoadoutPreset))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
