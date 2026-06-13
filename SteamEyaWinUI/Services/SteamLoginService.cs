using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamLoginService
{
    private readonly JwtTokenService _jwtTokenService = new();
    private readonly SteamPathService _steamPathService = new();
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
            progress?.Report("正在校验 EYA 令牌...");
            var token = _jwtTokenService.Validate(eyaToken);
            AppLog.Info($"EYA 令牌校验通过：SteamID={token.SteamId} 过期={token.ExpiresAt:yyyy-MM-dd HH:mm:ss}");

            progress?.Report("正在定位 Steam 安装目录...");
            var paths = _steamPathService.GetSteamPaths();
            var cachedAccountCandidates = _steamConfigService.GetLoginAccounts(paths);

            progress?.Report("正在加密 EYA 令牌...");
            var encryptedJwt = _steamCryptoService.EncryptToHex(eyaToken, accountName);
            var accountCrc32 = Crc32.ComputeSteamAccountKey(accountName);
            AppLog.Info($"令牌已加密（{encryptedJwt.Length} hex 字符）；ConnectCache key={accountCrc32}");

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report("正在写入 Steam 登录配置...");
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

            progress?.Report("正在启动 Steam...");
            _steamProcessService.LaunchSteamWithLogin(paths, accountName);

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
            progress?.Report("正在定位 Steam 安装目录...");
            var paths = _steamPathService.GetSteamPaths();

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report("正在恢复 Steam 登录配置...");
            _steamConfigService.RestoreLoginFiles(paths, account);

            progress?.Report("正在启动 Steam...");
            _steamProcessService.LaunchSteamWithLogin(paths, account.AccountName);

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
