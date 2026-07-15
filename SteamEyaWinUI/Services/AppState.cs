using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用级共享状态：服务单例、历史账号缓存、全局忙碌状态与更新检查协调。
/// 页面通过事件订阅状态变化（页面均为 NavigationCacheMode=Required 的常驻实例，无需退订）。
/// </summary>
internal static class AppState
{
    public static SteamLoginService LoginService { get; } = new();
    public static SteamWorkshopService WorkshopService { get; } = new();
    public static SteamLicenseClient LicenseClient { get; } = new();
    public static JwtTokenService JwtTokenService { get; } = new();
    public static SteamTokenOnlineValidationService TokenOnlineValidationService { get; } = new();
    public static AccountHistoryService AccountHistoryService { get; } = new();
    public static CsPremierScoreService PremierScoreService { get; } = new();
    public static CsLoadoutService LoadoutService { get; } = new();
    public static SteamProfileService ProfileService { get; } = new();
    public static SteamCredentialsAuthService CredentialsAuthService { get; } = new();
    public static GitHubUpdateService UpdateService { get; } = new();
    public static SettingsService SettingsService { get; } = new();
    public static Cs2CloudService Cs2CloudService { get; } = new();

    /// <summary>由 MainWindow 注入，向全局状态栏输出消息。</summary>
    public static Action<string, InfoBarSeverity>? StatusReporter { get; set; }

    /// <summary>
    /// 登录触发的缓存账号后台资料刷新已落盘（SteamLoginService.StartCachedProfileRefresh 完成且有更新）。
    /// 可能在后台线程触发，订阅方自行回 UI 线程。已缓存账号页订阅以自动重载，否则登录后立即打开该页
    /// 会一直停留在「未同步」的旧快照上。
    /// </summary>
    public static event Action? CachedLoginAccountsRefreshed;

    public static void NotifyCachedLoginAccountsRefreshed()
    {
        CachedLoginAccountsRefreshed?.Invoke();
    }

    /// <summary>登录页常驻实例，供历史页复用一键查询流程（保持与旧版一致的联动行为）。</summary>
    public static Pages.LoginPage? LoginPage { get; set; }

    public static void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusReporter?.Invoke(message, severity);
    }

    // ---------- 全局忙碌状态 ----------

    public static bool IsBusy { get; private set; }

    public static event Action<bool>? BusyChanged;

    public static void SetBusy(bool isBusy)
    {
        if (IsBusy == isBusy)
        {
            return;
        }

        IsBusy = isBusy;
        BusyChanged?.Invoke(isBusy);
    }

    // 当前忙碌操作的取消源。全部由 UI 线程访问（页面长流程的开始/取消/结束都在 UI 线程），
    // 无需额外同步。
    private static CancellationTokenSource? _busyCts;

    /// <summary>开始一段可取消的忙碌操作：置忙并新建 CTS，返回其 Token 供长任务传入。</summary>
    public static CancellationToken BeginBusyOperation()
    {
        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        SetBusy(true);
        return _busyCts.Token;
    }

    /// <summary>取消当前忙碌操作；无操作进行时为 no-op（取消按钮点击调用）。</summary>
    public static void CancelBusyOperation()
    {
        _busyCts?.Cancel();
    }

    /// <summary>结束忙碌操作：解忙并释放 CTS。幂等，可在 finally 中安全调用。</summary>
    public static void EndBusyOperation()
    {
        SetBusy(false);
        _busyCts?.Dispose();
        _busyCts = null;
    }

    // ---------- 历史账号缓存 ----------

    public static IReadOnlyList<SteamAccountHistoryItem> HistoryAccounts { get; private set; } = [];

    /// <summary>
    /// 希望历史页选中的 SteamID。历史页是懒加载的，登录/查询时发出的选中意图
    /// 在历史页首次构造前没有订阅者，存在这里供历史页构造时取用。
    /// </summary>
    public static string? PendingHistorySelection { get; set; }

    /// <summary>历史账号已重新加载；参数为希望选中的 SteamID（null 表示保持当前选择）。</summary>
    public static event Action<string?>? HistoryChanged;

    public static void ReloadHistory(string? selectSteamId = null)
    {
        try
        {
            HistoryAccounts = AccountHistoryService.Load();
        }
        catch (Exception ex)
        {
            HistoryAccounts = [];
            ShowStatus(Loc.Tf("AppState_HistoryLoadFailed_Format", ex.Message), InfoBarSeverity.Warning);
        }

        if (!string.IsNullOrWhiteSpace(selectSteamId))
        {
            PendingHistorySelection = selectSteamId;
        }

        HistoryChanged?.Invoke(selectSteamId);
    }

    public static SteamAccountHistoryItem? FindHistoryAccount(string? steamId)
    {
        return string.IsNullOrWhiteSpace(steamId)
            ? null
            : HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 更新检查协调 ----------

    public static GitHubUpdateInfo? LatestUpdate { get; private set; }

    public static bool IsCheckingForUpdates { get; private set; }

    public static string? UpdateCheckError { get; private set; }

    public static DateTimeOffset? UpdateCheckedAt { get; private set; }

    public static event Action? UpdateStateChanged;

    public static async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStateChanged?.Invoke();

        if (!isAutomatic)
        {
            ShowStatus(Loc.T("AppState_Update_Checking"), InfoBarSeverity.Informational);
        }

        try
        {
            var update = await UpdateService.CheckLatestAsync();
            LatestUpdate = update;
            UpdateCheckError = null;
            UpdateCheckedAt = update.CheckedAt;

            // 自动检查发现新版本只亮「关于」导航项红点（UpdateStateChanged → MainWindow.RefreshUpdateBadge），
            // 不再弹常驻横幅占用每页底部；手动检查才用状态栏明确反馈结果。
            if (update.IsUpdateAvailable)
            {
                if (!isAutomatic)
                {
                    ShowStatus(Loc.Tf("AppState_Update_Available_Format", update.LatestTag), InfoBarSeverity.Warning);
                }
            }
            else if (!isAutomatic)
            {
                ShowStatus(Loc.Tf("AppState_Update_UpToDate_Format", update.LatestTag), InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            UpdateCheckError = ex.Message;
            UpdateCheckedAt = DateTimeOffset.Now;

            if (!isAutomatic)
            {
                ShowStatus(Loc.Tf("AppState_Update_CheckFailed_Format", ex.Message), InfoBarSeverity.Error);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateStateChanged?.Invoke();
        }
    }

    public static async Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowStatus(Loc.T("AppState_Url_Invalid"), InfoBarSeverity.Error);
            return;
        }

        var opened = await Windows.System.Launcher.LaunchUriAsync(uri);
        ShowStatus(
            opened ? Loc.T("AppState_Url_Opened") : Loc.T("AppState_Url_OpenFailed"),
            opened ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
