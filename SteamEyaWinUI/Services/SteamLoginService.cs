using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamLoginService
{
    private readonly JwtTokenService _jwtTokenService = new();
    private readonly SteamPathService _steamPathService = new();
    private readonly SteamCryptoService _steamCryptoService = new();
    private readonly SteamConfigService _steamConfigService = new();
    private readonly SteamProcessService _steamProcessService = new();

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

            progress?.Report("正在加密 EYA 令牌...");
            var encryptedJwt = _steamCryptoService.EncryptToHex(eyaToken, accountName);
            var accountCrc32 = Crc32.ComputeSteamAccountKey(accountName);
            AppLog.Info($"令牌已加密（{encryptedJwt.Length} hex 字符）；ConnectCache key={accountCrc32}");

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report("正在写入 Steam 登录配置...");
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
}
