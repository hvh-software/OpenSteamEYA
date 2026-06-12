using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.System;

namespace SteamEyaWinUI.Pages;

public sealed partial class LoginPage : Page
{
    private SteamAccountData? _cachedAccountData;
    private string? _cachedLicenseKey;
    private SteamUpstreamServer? _cachedServer;
    private bool _suppressModeStatus;

    public LoginPage()
    {
        InitializeComponent();

        UpstreamServerBox.ItemsSource = SteamLicenseClient.Servers;
        UpstreamServerBox.SelectedIndex = 2;

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;

        // SelectorBarItem.IsSelected 在 XAML 解析期不可靠，显式设定初始模式。
        _suppressModeStatus = true;
        ModeSelector.SelectedItem = ManualModeItem;
        _suppressModeStatus = false;
        ApplyModeVisibility();

        UpdateAccountInfoFromCurrentInputs();
    }

    private bool IsAutoMode => ModeSelector.SelectedItem == AutoModeItem;

    private static void ShowStatus(string message, InfoBarSeverity severity)
    {
        AppState.ShowStatus(message, severity);
    }

    private void OnBusyChanged(bool isBusy)
    {
        var enabled = !isBusy;
        ModeSelector.IsEnabled = enabled;
        AccountNameBox.IsEnabled = enabled;
        EyaTokenBox.IsEnabled = enabled;
        UpstreamServerBox.IsEnabled = enabled;
        LicenseKeyBox.IsEnabled = enabled;
        ResolveLicenseButton.IsEnabled = enabled;
        ClearWorkshopButton.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
        OneClickQueryButton.IsEnabled = enabled;
    }

    /// <summary>从历史页载入账号到登录页（保持旧版行为：切手动模式并填充凭据）。</summary>
    public void LoadHistoryAccount(SteamAccountHistoryItem account)
    {
        ModeSelector.SelectedItem = ManualModeItem;
        AccountNameBox.Text = account.AccountName;
        EyaTokenBox.Text = account.EyaToken;
        UpdateAccountInfo(account.AccountName, account.EyaToken);
        ApplyAccountInfoProfile(account);

        ShowStatus(
            $"已载入历史账号：{account.AccountName}，上次登录 {FormatHelper.FormatDateTime(account.LastLoginAt)}。",
            InfoBarSeverity.Informational);
    }

    private async void ClearWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        ShowStatus("正在清除创意工坊订阅...", InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync();
            EnsureTokenValidForAction(eyaToken, "清除创意工坊订阅");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);
            await EnsureTokenAcceptedBySteamAsync(eyaToken, "清除创意工坊订阅");
            var count = await AppState.WorkshopService.ClearSubscriptionsAsync(eyaToken, progress);

            ShowStatus(
                $"已成功取消 {count} 个创意工坊订阅。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        ShowStatus("正在处理登录...", InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync();
            EnsureTokenValidForAction(eyaToken, "登录到 Steam");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);
            await EnsureTokenAcceptedBySteamAsync(eyaToken, "登录到 Steam");
            var result = await Task.Run(() => AppState.LoginService.Login(accountName, eyaToken, progress));
            var historyStatus = await SaveLoginHistoryAsync(result, eyaToken);
            ShowStatus(
                $"登录已启动。SteamID: {result.SteamId}，令牌剩余 {FormatHelper.FormatRemaining(result.Remaining)}{historyStatus}。",
                historyStatus.StartsWith("，但", StringComparison.Ordinal)
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("登录失败。", ex);
            ShowStatus($"{ex.Message}（诊断日志：{AppLog.LogFilePath}）", InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void ResolveLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        await ResolveLicenseInteractiveAsync();
    }

    private async Task ResolveLicenseInteractiveAsync()
    {
        AppState.SetBusy(true);
        ShowStatus("正在解析卡密...", InfoBarSeverity.Informational);

        try
        {
            var account = await ResolveLicenseAsync();
            var online = await ValidateTokenOnlineAsync(account.Token);
            ShowStatus(
                online.IsValid
                    ? $"已解析账户：{account.User}，SteamID: {account.SteamId}。"
                    : online.Status,
                online.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void OneClickQueryButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        ShowStatus("正在一键查询账号状态...", InfoBarSeverity.Informational);

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync();
            var score = await QueryAndSaveCsStatusAsync(accountName, eyaToken);
            ShowStatus(
                $"查询完成：优先分 {score.DisplayText}，CS2等级 {score.PlayerLevelText}，冷却 {score.CooldownText}，GC VAC {score.GcVacText}。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ManualPanel is null || AutoPanel is null)
        {
            return;
        }

        ApplyModeVisibility();
        UpdateAccountInfoFromCurrentInputs();

        if (!_suppressModeStatus)
        {
            ShowStatus(IsAutoMode ? "自动模式已启用。" : "手动模式已启用。", InfoBarSeverity.Informational);
        }
    }

    private void ApplyModeVisibility()
    {
        var isAutoMode = IsAutoMode;
        ManualPanel.Visibility = isAutoMode ? Visibility.Collapsed : Visibility.Visible;
        AutoPanel.Visibility = isAutoMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ManualCredentialBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsAutoMode)
        {
            UpdateAccountInfoFromCurrentInputs();
        }
    }

    private void LicenseKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _cachedAccountData = null;
        _cachedLicenseKey = null;
        ResolvedAccountBox.Text = "";

        if (IsAutoMode)
        {
            UpdateAccountInfoFromCurrentInputs();
        }
    }

    private void UpstreamServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cachedAccountData = null;
        _cachedLicenseKey = null;
        ResolvedAccountBox.Text = "";

        if (IsAutoMode)
        {
            UpdateAccountInfoFromCurrentInputs();
        }
    }

    private void AccountNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            EyaTokenBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void LicenseKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
        {
            e.Handled = true;
            await ResolveLicenseInteractiveAsync();
        }
    }

    private async Task<(string AccountName, string EyaToken)> GetCredentialsAsync()
    {
        if (IsAutoMode)
        {
            var account = await ResolveLicenseAsync();
            return (account.User, FormatHelper.NormalizeToken(account.Token));
        }

        var accountName = AccountNameBox.Text.Trim();
        var eyaToken = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException("请输入账户名称。");
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new InvalidOperationException("请输入 EYA 令牌。");
        }

        return (accountName, eyaToken);
    }

    private async Task<SteamAccountData> ResolveLicenseAsync()
    {
        var licenseKey = LicenseKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new InvalidOperationException("请输入卡密。");
        }

        var server = GetSelectedServer();
        if (_cachedAccountData is not null &&
            string.Equals(_cachedLicenseKey, licenseKey, StringComparison.Ordinal) &&
            Equals(_cachedServer, server))
        {
            return _cachedAccountData;
        }

        var account = await AppState.LicenseClient.GetAccountDataAsync(licenseKey, server);
        _cachedAccountData = account;
        _cachedLicenseKey = licenseKey;
        _cachedServer = server;
        ResolvedAccountBox.Text = $"{account.User}  ({account.SteamId})";
        UpdateAccountInfo(account.User, account.Token);
        await UpdateAccountProfileAsync(account.User, account.Token);
        return account;
    }

    private SteamUpstreamServer GetSelectedServer()
    {
        return UpstreamServerBox.SelectedItem as SteamUpstreamServer
            ?? throw new InvalidOperationException("请选择上游服务器。");
    }

    /// <summary>
    /// 一键查询并保存 CS 账号状态。历史页也会调用此方法，
    /// 与旧版一致：查询过程同步更新登录页右侧的账号信息面板。
    /// </summary>
    public async Task<CsPremierScoreResult> QueryAndSaveCsStatusAsync(
        string accountName,
        string eyaToken)
    {
        eyaToken = FormatHelper.NormalizeToken(eyaToken);
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (!tokenInfo.IsValid)
        {
            throw new InvalidOperationException($"{tokenInfo.Status}无法一键查询。");
        }

        var steamId = tokenInfo.SteamId
            ?? throw new InvalidOperationException("EYA 令牌缺少 SteamID，无法查询账号状态。");

        UpdateAccountInfo(accountName, eyaToken);
        await UpdateAccountProfileAsync(accountName, eyaToken);
        AccountInfoAvailabilityText.Text = "正在验证并查询";
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);
        AccountInfoPremierScoreText.Text = "正在查询";
        AccountInfoCsLevelText.Text = "正在查询";
        AccountInfoCooldownStatusText.Text = "正在查询";

        CsPremierScoreResult score;
        SteamTokenOnlineValidationResult online;
        try
        {
            score = await AppState.PremierScoreService.QueryAsync(eyaToken, steamId);
            online = new SteamTokenOnlineValidationResult(true, "Steam 已接受该令牌。");
            AccountInfoAvailabilityText.Text = "有效";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            AccountInfoAvailabilityText.Text = "无效";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            throw new InvalidOperationException($"{ex.Message}无法一键查询。", ex);
        }
        catch
        {
            AccountInfoAvailabilityText.Text = "未验证";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);
            throw;
        }

        AppState.AccountHistoryService.SaveCsAccountStatus(
            accountName,
            steamId,
            eyaToken,
            tokenInfo.ExpiresAt,
            score,
            online);

        AppState.ReloadHistory(steamId);
        AccountInfoPremierScoreText.Text = score.DisplayText;
        AccountInfoCsLevelText.Text = score.PlayerLevelText;
        AccountInfoCooldownStatusText.Text = score.CooldownStatusText;
        ApplyStoredAccountInfoProfile(steamId);
        return score;
    }

    private async Task<string> SaveLoginHistoryAsync(LoginResult result, string eyaToken)
    {
        try
        {
            await AppState.AccountHistoryService.SaveLoginAsync(
                result.AccountName,
                result.SteamId,
                eyaToken,
                result.ExpiresAt);
            AppState.ReloadHistory(result.SteamId);
            ApplyStoredAccountInfoProfile(result.SteamId);
            return "，已更新历史账号";
        }
        catch (Exception ex)
        {
            return $"，但历史账号保存失败：{ex.Message}";
        }
    }

    private async Task UpdateAccountProfileAsync(string accountName, string eyaToken)
    {
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return;
        }

        var storedAccount = AppState.FindHistoryAccount(tokenInfo.SteamId);
        if (storedAccount is not null)
        {
            ApplyAccountInfoProfile(storedAccount);
            if (!string.IsNullOrWhiteSpace(storedAccount.PersonaName) &&
                (!string.IsNullOrWhiteSpace(storedAccount.AvatarPath) ||
                    !string.IsNullOrWhiteSpace(storedAccount.AvatarUrl)))
            {
                return;
            }
        }

        try
        {
            var profile = await AppState.AccountHistoryService.GetProfilePreviewAsync(
                accountName,
                tokenInfo.SteamId,
                eyaToken,
                tokenInfo.ExpiresAt);
            if (profile is not null)
            {
                ApplyAccountInfoProfile(profile);
            }
        }
        catch
        {
            // Profile sync is decorative; token validation and account actions should continue.
        }
    }

    private void ApplyStoredAccountInfoProfile(string steamId)
    {
        var account = AppState.FindHistoryAccount(steamId);
        if (account is not null)
        {
            ApplyAccountInfoProfile(account);
        }
    }

    private void ApplyAccountInfoProfile(SteamAccountHistoryItem? account)
    {
        if (account is null)
        {
            AccountInfoAvatar.ProfilePicture = null;
            AccountInfoAvatar.DisplayName = "未填写";
            AccountInfoPersonaText.Text = "Steam 昵称未同步";
            AccountInfoPremierScoreText.Text = "未查询";
            AccountInfoCsLevelText.Text = "未查询";
            AccountInfoCooldownStatusText.Text = "未查询";
            return;
        }

        AccountInfoAvatar.DisplayName = account.AccountTitle;
        AccountInfoAvatar.ProfilePicture = account.AvatarImage;
        AccountInfoPersonaText.Text = account.PersonaDisplayName;
        AccountInfoPremierScoreText.Text = account.CompetitiveScoreText;
        AccountInfoCsLevelText.Text = account.CsPlayerLevelText;
        AccountInfoCooldownStatusText.Text = account.CooldownStatusText;
    }

    private void EnsureTokenValidForAction(string eyaToken, string actionName)
    {
        var info = AppState.JwtTokenService.Inspect(eyaToken);
        if (info.IsValid)
        {
            return;
        }

        if (info.Status == "EYA 令牌已过期。" && actionName == "清除创意工坊订阅")
        {
            throw new InvalidOperationException("EYA 令牌已过期，无法清除创意工坊订阅。");
        }

        throw new InvalidOperationException($"{info.Status}无法{actionName}。");
    }

    private void UpdateAccountInfoFromCurrentInputs()
    {
        if (AccountInfoUserText is null)
        {
            return;
        }

        if (IsAutoMode)
        {
            if (_cachedAccountData is not null)
            {
                UpdateAccountInfo(
                    _cachedAccountData.User,
                    _cachedAccountData.Token);
            }
            else
            {
                UpdateAccountInfo(null, null);
            }

            return;
        }

        UpdateAccountInfo(
            AccountNameBox.Text.Trim(),
            FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim()));
    }

    private void UpdateAccountInfo(string? userName, string? token)
    {
        AccountInfoUserText.Text = string.IsNullOrWhiteSpace(userName) ? "未填写" : userName;
        ApplyAccountInfoProfile(null);
        AccountInfoAvatar.DisplayName = string.IsNullOrWhiteSpace(userName) ? "未填写" : userName;

        if (string.IsNullOrWhiteSpace(token))
        {
            AccountInfoSteamIdText.Text = "未解析";
            AccountInfoExpiresText.Text = "未解析";
            AccountInfoAvailabilityText.Text = "未验证";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);
            return;
        }

        var info = AppState.JwtTokenService.Inspect(token);
        AccountInfoSteamIdText.Text = string.IsNullOrWhiteSpace(info.SteamId) ? "未解析" : info.SteamId;
        AccountInfoExpiresText.Text = info.ExpiresAt.HasValue
            ? info.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : "未解析";
        AccountInfoAvailabilityText.Text = "未验证";
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);

        if (!string.IsNullOrWhiteSpace(info.SteamId))
        {
            ApplyStoredAccountInfoProfile(info.SteamId);
        }
    }

    private async Task EnsureTokenAcceptedBySteamAsync(string eyaToken, string actionName)
    {
        var online = await ValidateTokenOnlineAsync(eyaToken);
        if (online.IsValid)
        {
            return;
        }

        throw new InvalidOperationException($"{online.Status}无法{actionName}。");
    }

    private async Task<SteamTokenOnlineValidationResult> ValidateTokenOnlineAsync(string eyaToken)
    {
        AccountInfoAvailabilityText.Text = "正在验证";
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);

        try
        {
            var result = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken);
            AccountInfoAvailabilityText.Text = result.IsValid ? "有效" : "无效";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
                result.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return result;
        }
        catch (Exception ex)
        {
            var result = new SteamTokenOnlineValidationResult(false, $"Steam 在线验证失败：{ex.Message}");
            AccountInfoAvailabilityText.Text = "无效";
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            return result;
        }
    }
}
