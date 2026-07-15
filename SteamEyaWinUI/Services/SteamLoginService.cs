using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamLoginService
{
    private readonly JwtTokenService _jwtTokenService = new();
    private readonly SteamCryptoService _steamCryptoService = new();
    private readonly SteamConfigService _steamConfigService = new();
    private readonly SteamProcessService _steamProcessService = new();
    private readonly SteamLoginCacheService _loginCacheService = new();
    private readonly AccountHistoryService _accountHistoryService = new();

    public LoginResult Login(string accountName, string eyaToken, IProgress<string>? progress = null)
    {
        AppLog.Info(
            $"==== 开始上号：账号=\"{accountName}\"  OS={Environment.OSVersion}  64位进程={Environment.Is64BitProcess} ====");
        try
        {
            progress?.Report(Loc.T("Steam_Progress_ValidatingToken"));
            var token = _jwtTokenService.Validate(eyaToken);
            AppLog.Info($"EYA 令牌校验通过：SteamID={token.SteamId} 过期={token.ExpiresAt:yyyy-MM-dd HH:mm:ss}");

            progress?.Report(Loc.T("Steam_Progress_LocatingInstall"));
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();
            var cachedAccountCandidates = _steamConfigService.GetLoginAccounts(paths);

            progress?.Report(Loc.T("Steam_Progress_EncryptingToken"));
            var encryptedJwt = _steamCryptoService.EncryptToHex(eyaToken, accountName);
            var accountCrc32 = Crc32.ComputeSteamAccountKey(accountName);
            AppLog.Info($"令牌已加密（{encryptedJwt.Length} hex 字符）；ConnectCache key={accountCrc32}");

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report(Loc.T("Steam_Progress_WritingConfig"));
            if (cachedAccountCandidates.Count == 0)
            {
                cachedAccountCandidates = _steamConfigService.GetLoginAccounts(paths);
            }

            _loginCacheService.MarkEyaLogin(accountName, token.SteamId);
            CacheLoginAccounts(cachedAccountCandidates, accountName, token.SteamId);
            _steamConfigService.UpdateLoginFiles(
                paths,
                accountName,
                token.SteamId,
                encryptedJwt,
                accountCrc32);

            progress?.Report(Loc.T("Steam_Progress_StartingSteam"));
            _steamProcessService.LaunchSteamWithLogin(paths, accountName);

            TryPushCs2Cloud(paths, token.SteamId, progress);

            AppLog.Info("==== 上号流程完成（已请求启动 Steam）====");
            return new LoginResult(accountName, token.SteamId, token.ExpiresAt);
        }
        catch (Exception ex)
        {
            AppLog.Error("上号流程失败。", ex);
            throw;
        }
    }

    public IReadOnlyList<CachedSteamLoginAccount> GetCachedLoginAccounts()
    {
        return _loginCacheService.LoadAll()
            .Where(account => !IsKnownEyaAccount(account))
            .ToList();
    }

    public CachedSteamLoginAccount RestoreCachedLogin(
        CachedSteamLoginAccount account,
        IProgress<string>? progress = null)
    {
        AppLog.Info("==== 开始恢复缓存 Steam 账号 ====");

        try
        {
            progress?.Report(Loc.T("Steam_Progress_LocatingInstall"));
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report(Loc.T("Steam_Progress_RestoringConfig"));
            _steamConfigService.RestoreLoginFiles(paths, account);

            progress?.Report(Loc.T("Steam_Progress_StartingSteam"));
            _steamProcessService.LaunchSteamWithLogin(paths, account.AccountName);

            TryPushCs2Cloud(paths, account.SteamId, progress);

            AppLog.Info($"==== 已请求恢复缓存账号：{account.AccountName} ({account.SteamId}) ====");
            return account;
        }
        catch (Exception ex)
        {
            AppLog.Error("恢复缓存 Steam 账号失败。", ex);
            throw;
        }
    }

    public int DeleteCachedLoginAccounts(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        return _loginCacheService.Delete(accounts);
    }

    public int ClearCachedLoginAccounts()
    {
        return _loginCacheService.ClearAll();
    }

    public Task<int> RefreshCachedLoginProfilesAsync(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        return _loginCacheService.RefreshProfilesAsync(accounts);
    }

    // 若开启「登录时同步 CS2 设置」，登录后把来源账号的 CS2 配置强推到目标账号的 Steam 云。
    // Steam 刚启动尚未登好，ForcePush 内部会重试等待；放后台执行，不阻塞上号返回，失败只记日志。
    private void TryPushCs2Cloud(SteamPaths paths, string targetSteamId, IProgress<string>? progress)
    {
        try
        {
            // 直接用全局单例（而非字段捕获）：AppState.LoginService 是 AppState 静态初始化里第一个 new 的，
            // 那时 SettingsService/Cs2CloudService 尚未赋值，字段初始化器会捕获到 null；此处在真正登录时才求值，
            // 静态初始化早已完成。共享单例仍能让 settings.json 读写与设置页保存串行化（实例级锁）。
            var settings = AppState.SettingsService.Load();
            if (!settings.Cs2SyncOnLogin || string.IsNullOrWhiteSpace(settings.Cs2SyncSourceSteamId))
            {
                return;
            }

            var source = settings.Cs2SyncSourceSteamId;
            _ = Task.Run(() => AppState.Cs2CloudService.PushSourceForLogin(paths, source, targetSteamId, progress));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CS2 云推送调度失败，忽略：{ex.Message}");
        }
    }

    private void CacheLoginAccounts(
        IReadOnlyList<CachedSteamLoginAccount> accounts,
        string nextAccountName,
        string nextSteamId)
    {
        var filtered = accounts
            .Where(account =>
                !string.Equals(account.AccountName, nextAccountName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(account.SteamId, nextSteamId, StringComparison.OrdinalIgnoreCase) &&
                !IsKnownEyaAccount(account))
            .ToList();

        if (filtered.Count == 0)
        {
            return;
        }

        var saved = _loginCacheService.SaveMany(filtered);
        AppLog.Info($"Cached {saved.Count} non-EYA Steam account(s) for restore.");
        StartCachedProfileRefresh(saved);
    }

    private void StartCachedProfileRefresh(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await _loginCacheService.RefreshProfilesAsync(accounts);
                AppLog.Info($"Updated cached Steam profile data for {updated} account(s).");
                if (updated > 0)
                {
                    // 通知已缓存账号页重载：否则刷新虽已落盘，打开着的页面仍停留在「未同步」旧快照。
                    AppState.NotifyCachedLoginAccountsRefreshed();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"同步缓存账号头像失败：{ex.Message}");
            }
        });
    }

    private bool IsKnownEyaAccount(CachedSteamLoginAccount account)
    {
        try
        {
            if (_loginCacheService.IsEyaLogin(account))
            {
                return true;
            }

            return _accountHistoryService.Load().Any(historyAccount =>
                string.Equals(historyAccount.SteamId, account.SteamId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(historyAccount.AccountName, account.AccountName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
