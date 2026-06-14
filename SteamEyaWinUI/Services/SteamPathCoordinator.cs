using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Steam 安装路径的解析、持久化与失败兜底的总编排：
///   1. 优先用 settings 里已持久化的路径（仍含 steam.exe 才算有效）；
///   2. 失效或为空 → 自动探测，成功则写回 settings——首次启动即缓存，之后上号直接复用，不再每次重新探测；
///   3. 仍找不到 → 弹框让用户手动选择含 steam.exe 的目录并持久化。
/// 非 UI 的 <see cref="TryResolveInstallPath"/> / <see cref="ResolvePathsOrThrow"/> 可在后台线程调用；
/// <see cref="EnsureResolvedAsync"/> 及其弹框只能在 UI 线程调用。
/// </summary>
internal static class SteamPathCoordinator
{
    private static readonly SteamPathService _service = new();

    // 同一时刻只允许一个「手动选择」弹框（启动检测与上号前检测可能并发触发；
    // 同一 XamlRoot 二次 ShowAsync 会直接抛异常）。
    private static readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>
    /// 非 UI：返回可用的 Steam 安装目录——持久化值优先，失效/为空则自动探测并写回 settings；都拿不到返回 null。
    /// 仅做磁盘/注册表 I/O，后台线程调用安全。
    /// </summary>
    public static string? TryResolveInstallPath()
    {
        var settings = AppState.SettingsService.Load();
        var persisted = settings.SteamInstallPath;
        if (SteamPathService.ContainsSteamExe(persisted))
        {
            // 规整成磁盘真实大小写并回写——把旧版本/HKCU 写下的全小写正斜杠路径一次性升级，显示更协调。
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(persisted!));
        }

        if (!string.IsNullOrWhiteSpace(persisted))
        {
            AppLog.Warn($"已持久化的 Steam 安装目录失效（找不到 steam.exe），重新自动探测：\"{persisted}\"");
        }

        var detected = _service.AutoDetectInstallPath();
        if (detected is not null)
        {
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(detected));
        }

        return null;
    }

    // 仅当与已存值不同才写盘，避免每次解析都落盘；顺带把全小写/正斜杠的旧值升级成磁盘真实大小写。
    private static string PersistCanonical(AppSettings settings, string canonicalPath)
    {
        if (!string.Equals(settings.SteamInstallPath, canonicalPath, StringComparison.Ordinal))
        {
            settings.SteamInstallPath = canonicalPath;
            AppState.SettingsService.Save(settings);
            AppLog.Info($"持久化 Steam 安装目录：\"{canonicalPath}\"");
        }

        return canonicalPath;
    }

    /// <summary>
    /// 非 UI：解析为完整 <see cref="SteamPaths"/>；解析不到则抛。
    /// 这是上号流程（后台线程）的安全网——正常情况下 UI 已在上号前用 <see cref="EnsureResolvedAsync"/> 解析并持久化。
    /// </summary>
    public static SteamPaths ResolvePathsOrThrow()
    {
        var installPath = TryResolveInstallPath()
            ?? throw new InvalidOperationException(Loc.T("SteamPath_Error_NotResolved"));
        return _service.BuildPaths(installPath);
    }

    /// <summary>
    /// UI：确保已解析出可用的 Steam 安装目录。先尝试持久化/自动探测（后台线程），不行再弹框让用户选择。
    /// 返回 true=已就绪，false=用户取消未设置。可在启动时与每次上号前调用。
    /// </summary>
    public static async Task<bool> EnsureResolvedAsync()
    {
        if (await Task.Run(TryResolveInstallPath) is not null)
        {
            return true;
        }

        await _promptGate.WaitAsync();
        try
        {
            // 进闸后复检：可能已被另一处（如启动检测）解析好，避免重复弹框。
            if (await Task.Run(TryResolveInstallPath) is not null)
            {
                return true;
            }

            return await PromptForManualPathAsync();
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <summary>当前持久化的 Steam 安装目录（供设置页显示，规整为磁盘真实大小写）；未设置返回 null。</summary>
    public static string? GetPersistedInstallPath()
    {
        var path = AppState.SettingsService.Load().SteamInstallPath;
        return string.IsNullOrWhiteSpace(path) ? null : SteamPathService.NormalizeInstallPath(path);
    }

    /// <summary>
    /// UI：设置页「更改 Steam 路径」——直接打开文件夹选择器（不弹"未找到"对话框，用户是主动来改的），
    /// 校验含 steam.exe 后持久化。返回是否成功更改。供多 Steam 用户显式指定要用哪一个。
    /// </summary>
    public static async Task<bool> PickAndPersistManuallyAsync()
    {
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return false; // 用户取消
        }

        if (!SteamPathService.ContainsSteamExe(folder.Path))
        {
            AppLog.Warn($"用户选择的目录不含 steam.exe：\"{folder.Path}\"");
            AppState.ShowStatus(Loc.T("SteamPath_Dialog_InvalidContent"), InfoBarSeverity.Error);
            return false;
        }

        var normalized = SteamPathService.NormalizeInstallPath(folder.Path);
        var settings = AppState.SettingsService.Load();
        settings.SteamInstallPath = normalized;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"用户在设置页手动更改 Steam 安装目录：\"{normalized}\"");
        return true;
    }

    private static async Task<bool> PromptForManualPathAsync()
    {
        var xamlRoot = MainWindow.Instance?.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            AppLog.Warn("需要手动选择 Steam 目录，但窗口尚未就绪，跳过弹框（稍后上号时会再次提示）。");
            return false;
        }

        var content = Loc.T("SteamPath_Dialog_Content");
        while (true)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.T("SteamPath_Dialog_Title"),
                Content = content,
                PrimaryButtonText = Loc.T("SteamPath_Dialog_Pick"),
                CloseButtonText = Loc.T("SteamPath_Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                AppLog.Info("用户取消了手动选择 Steam 安装目录。");
                return false;
            }

            var folder = await PickFolderAsync();
            if (folder is null)
            {
                // 选择器被取消：回到提示框，让用户决定重选还是彻底取消。
                continue;
            }

            if (SteamPathService.ContainsSteamExe(folder.Path))
            {
                var normalized = SteamPathService.NormalizeInstallPath(folder.Path);
                var settings = AppState.SettingsService.Load();
                settings.SteamInstallPath = normalized;
                AppState.SettingsService.Save(settings);
                AppLog.Info($"用户手动指定并持久化 Steam 安装目录：\"{normalized}\"");
                return true;
            }

            AppLog.Warn($"用户选择的目录不含 steam.exe：\"{folder.Path}\"");
            content = Loc.T("SteamPath_Dialog_InvalidContent");
        }
    }

    private static async Task<StorageFolder?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*"); // FolderPicker 必须至少有一个过滤项，否则 PickSingleFolderAsync 直接返回 null
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            // 无包装 WinUI：选择器必须绑定到窗口句柄，否则抛 COM 异常。
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            return await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("打开 Steam 目录选择器失败。", ex);
            AppState.ShowStatus(Loc.T("SteamPath_PickFail"), InfoBarSeverity.Error);
            return null;
        }
    }
}
