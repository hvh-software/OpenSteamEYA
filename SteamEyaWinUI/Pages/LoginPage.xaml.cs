using System.ComponentModel;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.System;

namespace SteamEyaWinUI.Pages;

public sealed partial class LoginPage : Page, INotifyPropertyChanged
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private SteamAccountData? _cachedAccountData;
    private string? _cachedLicenseKey;
    private SteamUpstreamServer? _cachedServer;
    private bool _suppressModeStatus;

    // 历史账号保存是否失败：SaveLoginHistoryAsync 置位，登录消息据此决定 Warning/Success 严重度
    // （本地化后历史后缀文案不再含固定中文前缀，故改用此标志替代原先的字符串前缀判断）。
    private bool _lastLoginHistorySaveFailed;

    public LoginPage()
    {
        InitializeComponent();

        UpstreamServerBox.ItemsSource = SteamLicenseClient.Servers;
        UpstreamServerBox.SelectedIndex = 2;

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;
        Loc.LanguageChanged += OnLanguageChanged;

        // SelectorBarItem.IsSelected 在 XAML 解析期不可靠，显式设定初始模式。
        _suppressModeStatus = true;
        ModeSelector.SelectedItem = ManualModeItem;
        _suppressModeStatus = false;
        ApplyLanguageModeAvailability();

        UpdateAccountInfoFromCurrentInputs();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private bool IsAutoMode => ModeSelector.SelectedItem == AutoModeItem;

    private bool IsCredsMode => ModeSelector.SelectedItem == CredsModeItem;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；右侧账号信息面板的命令式文本（用户名/SteamID/过期/可用状态/分数/等级/冷却）
            // 重跑下面两个方法即可让已显示的“未填写/未解析/未验证”等占位换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            ApplyLanguageModeAvailability();
            UpdateAccountInfoFromCurrentInputs();
            OnBusyChanged(AppState.IsBusy);
        });
    }

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
        ApplyLoadoutButton.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
        OneClickQueryButton.IsEnabled = enabled;

        // 取消按钮反其道而行：仅忙碌时出现且保持可用，让用户中断长任务。
        CancelButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(Loc.T("Login_Status_Cancelling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
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
            Loc.Tf("Login_Status_HistoryLoaded_Format", account.AccountName, FormatHelper.FormatDateTime(account.LastLoginAt)),
            InfoBarSeverity.Informational);
    }

    // 一键装配：用“配装”页保存的那套预设 + 当前账号 token，把武器写入两阵营。取代原“装备 R8”。
    private async void ApplyLoadoutButton_Click(object sender, RoutedEventArgs e)
    {
        var preset = AppState.SettingsService.Load().Loadout;
        if (preset.T.Count == 0 && preset.Ct.Count == 0)
        {
            ShowStatus(Loc.T("Login_Loadout_EmptyPreset"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_ApplyLoadout");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);

            var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
            var steamId = tokenInfo.SteamId
                ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdLoadout"));

            ShowStatus(Loc.T("Login_Status_ApplyingLoadout"), InfoBarSeverity.Informational);
            var result = await AppState.LoadoutService.ApplyPresetAsync(
                preset,
                eyaToken,
                steamId,
                cancellationToken);

            if (result.IsSuccess)
            {
                ShowStatus(Loc.Tf("Login_Status_LoadoutApplied_Format", result.Confirmed), InfoBarSeverity.Success);
            }
            else
            {
                var detail = string.Join("、", result.Failures.Take(6));
                if (result.Failures.Count > 6)
                {
                    detail += "…";
                }

                ShowStatus(
                    Loc.Tf("Login_Status_LoadoutPartial_Format", result.Confirmed, result.Requested, detail),
                    InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoadoutCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // 一键个性化：用「个性化」页保存的昵称 + 简介（真名 / 概要）+ 头像，把当前账号资料设置成这套；
    // 勾选了「清理曾用名」时在最后清空曾用名记录。
    private async void PersonalizeButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        var personaName = settings.PersonaName;
        var avatarPath = AppState.SettingsService.PersonalizationAvatarPath;
        var hasName = !string.IsNullOrWhiteSpace(personaName);
        var hasRealName = !string.IsNullOrWhiteSpace(settings.ProfileRealName);
        var hasSummary = !string.IsNullOrWhiteSpace(settings.ProfileSummary);
        var hasAvatar = File.Exists(avatarPath);
        var clearAliases = settings.ClearAliasHistoryOnPersonalize;

        if (!hasName && !hasRealName && !hasSummary && !hasAvatar && !clearAliases)
        {
            ShowStatus(Loc.T("Login_Personalize_Empty"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_Personalize");
            UpdateAccountInfo(accountName, eyaToken);

            var result = await AppState.ProfileService.ApplyAsync(
                eyaToken,
                new SteamProfileApplyRequest(
                    hasName ? personaName : null,
                    hasRealName ? settings.ProfileRealName : null,
                    hasSummary ? settings.ProfileSummary : null,
                    hasAvatar ? avatarPath : null,
                    clearAliases),
                progress,
                cancellationToken);

            if (result.IsFullSuccess)
            {
                ShowStatus(Loc.T("Login_Status_Personalized"), InfoBarSeverity.Success);
            }

            // 应用成功的项用「已知的新值」直接写回本地记录并刷新界面——不走再抓：
            // 社区资料接口有边缘缓存，改名后立刻抓取可能仍返回旧值，反而把记录「倒退」回去。
            if (result.NameApplied || result.AvatarApplied)
            {
                var steamId = AppState.JwtTokenService.Inspect(eyaToken).SteamId;
                if (!string.IsNullOrWhiteSpace(steamId))
                {
                    await Task.Run(() => AppState.AccountHistoryService.UpdateProfileLocally(
                        steamId,
                        result.NameApplied ? personaName : null,
                        result.AvatarApplied ? avatarPath : null));
                    AppState.ReloadHistory();
                    if (string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyStoredAccountInfoProfile(steamId);
                    }
                }
            }

            if (!result.IsFullSuccess)
            {
                var errors = new List<string>();
                if (result.ProfileRequested && !result.ProfileApplied)
                {
                    errors.Add(result.ProfileError ?? Loc.T("Profile_Error_Unknown"));
                }

                if (result.AvatarRequested && !result.AvatarApplied)
                {
                    errors.Add(result.AvatarError ?? Loc.T("Profile_Error_Unknown"));
                }

                if (result.AliasClearRequested && !result.AliasesCleared)
                {
                    errors.Add(result.AliasClearError ?? Loc.T("Profile_Error_Unknown"));
                }

                ShowStatus(
                    Loc.Tf("Login_Status_PersonalizePartial_Format", string.Join("; ", errors)),
                    InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_PersonalizeCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // 账密模式（调试）：用账号密码经 Web API 换取 EYA 令牌，存入历史并填进手动模式。
    private async void CredFetchButton_Click(object sender, RoutedEventArgs e)
    {
        var accountName = CredAccountNameBox.Text.Trim();
        var password = CredPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            ShowStatus(Loc.T("Creds_Error_AccountRequired"), InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowStatus(Loc.T("Creds_Error_PasswordRequired"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var result = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
                accountName, password, PromptGuardCodeAsync, progress, cancellationToken);

            var token = FormatHelper.NormalizeToken(result.RefreshToken);
            var info = AppState.JwtTokenService.Inspect(token);

            // 保存到历史账号。
            await AppState.AccountHistoryService.SaveLoginAsync(
                result.AccountName, result.SteamId, token, info.ExpiresAt);
            AppState.ReloadHistory(result.SteamId);

            // 填进手动模式并切过去，方便接着上号/查询；清空密码不留存。
            _suppressModeStatus = true;
            ModeSelector.SelectedItem = ManualModeItem;
            _suppressModeStatus = false;
            AccountNameBox.Text = result.AccountName;
            EyaTokenBox.Text = token;
            CredPasswordBox.Password = string.Empty;
            UpdateAccountInfo(result.AccountName, token);

            ShowStatus(Loc.Tf("Creds_Status_Saved_Format", result.AccountName), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Creds_Status_Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("账密获取 EYA 令牌失败。", ex);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // 令牌验证器需要输码时，弹窗向用户索取邮箱/手机验证码；取消返回 null。
    private async Task<string?> PromptGuardCodeAsync(SteamGuardPrompt prompt, CancellationToken cancellationToken)
    {
        var isMobile = prompt.Type == SteamGuardType.DeviceCode;

        var codeBox = new TextBox
        {
            PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
            IsSpellCheckEnabled = false,
            MaxLength = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.AssociatedMessage)
                ? Loc.T(isMobile ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage")
                : prompt.AssociatedMessage,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            Title = Loc.T(isMobile ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
            Content = panel,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? codeBox.Text.Trim() : null;
    }

    private async void ClearWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ClearingWorkshop"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_ClearWorkshop");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);

            // 不再前置 EnsureTokenAcceptedBySteamAsync：ClearSubscriptionsAsync 内部已完整握手，
            // 令牌被拒会在下方按 SteamCmException.IsTokenFailure 处理，避免无谓的双重握手。
            var count = await AppState.WorkshopService.ClearSubscriptionsAsync(
                eyaToken,
                progress,
                cancellationToken);

            ShowStatus(
                Loc.Tf("Login_Status_WorkshopCleared_Format", count),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_ClearWorkshopCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            // 令牌被 Steam 拒绝：与前置验证失败时一致地标红可用状态，再报错。
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            ShowStatus($"{ex.Message}{Loc.T("Login_Error_CannotClearWorkshopSuffix")}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        // 首次会自动检测并持久化 Steam 路径；失效则重新检测；都不行才弹框让用户选。用户取消即中止上号。
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_Processing"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
            UpdateAccountInfo(accountName, eyaToken);
            // 这一次抓取的 persona/头像直接传给 SaveLoginAsync，避免历史保存时重复抓取一遍。
            // triggerBackgroundRefresh:false——登录耗时数十秒，此刻兜底刷新写入的新值会被登录结束时
            // SaveLoginAsync 回写的本快照（旧值）覆盖，兜底刷新改在 SaveLoginHistoryAsync 保存之后做。
            var profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
            await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
            var result = await Task.Run(
                () => AppState.LoginService.Login(accountName, eyaToken, progress),
                cancellationToken);
            var historyStatus = await SaveLoginHistoryAsync(result, eyaToken, profile);
            ShowStatus(
                Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining), historyStatus),
                _lastLoginHistorySaveFailed
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("登录失败。", ex);
            ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    /// <summary>
    /// 供历史页「快速登录」直接用指定账号上号：与「登录到 Steam」相同的流程
    /// （结构校验 → 在线验证，网络故障会跳过 → 写配置并启动 Steam → 写入历史）。
    /// 调用方负责忙碌状态与状态/异常呈现；异常原样抛出。
    /// </summary>
    public async Task<LoginResult> QuickLoginAsync(
        string accountName,
        string eyaToken,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        // 与「登录到 Steam」一致：先确保 Steam 路径已解析（首启检测/持久化/失效重测/弹框）。
        // 用户取消选择视为取消上号，交由调用方按 OperationCanceledException 给出中性提示。
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            throw new OperationCanceledException();
        }

        eyaToken = FormatHelper.NormalizeToken(eyaToken);
        EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
        UpdateAccountInfo(accountName, eyaToken);
        // 本次抓取的 persona/头像直接传给 SaveLoginAsync，避免历史保存时重复抓取。
        // triggerBackgroundRefresh:false 的理由同「登录到 Steam」：兜底刷新移到保存之后，防旧快照覆盖新值。
        var profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
        await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
        var result = await Task.Run(
            () => AppState.LoginService.Login(accountName, eyaToken, progress),
            cancellationToken);
        await SaveLoginHistoryAsync(result, eyaToken, profile);
        return result;
    }

    private async void ResolveLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        await ResolveLicenseInteractiveAsync();
    }

    private async Task ResolveLicenseInteractiveAsync()
    {
        // 走 BeginBusyOperation 而非裸 SetBusy：忙碌时出现的取消按钮才有真正可取消的 CTS。
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);

        try
        {
            var account = await ResolveLicenseAsync(cancellationToken);
            var online = await ValidateTokenOnlineAsync(account.Token, cancellationToken);
            ShowStatus(
                online.IsValid
                    ? Loc.Tf("Login_Status_LicenseResolved_Format", account.User, account.SteamId)
                    : online.Status,
                online.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        // 仅在用户真的按了取消时报「已取消」；HTTP 超时抛的 TaskCanceledException（此 token 未取消）
        // 落到下面的通用错误分支，避免把「服务器不可达超时」误报成用户取消。
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void OneClickQueryButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_QueryingAccount"), InfoBarSeverity.Informational);

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            var score = await QueryAndSaveCsStatusAsync(accountName, eyaToken, cancellationToken);
            ShowStatus(
                Loc.Tf("Login_Status_QueryDone_Format", score.DisplayText, score.PlayerLevelText, score.CooldownText, score.GcVacText),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_QueryCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ManualPanel is null || AutoPanel is null || CredsPanel is null || ActionButtonGrid is null)
        {
            return;
        }

        ApplyModeVisibility();
        UpdateAccountInfoFromCurrentInputs();

        if (!_suppressModeStatus)
        {
            var statusKey = IsCredsMode
                ? "Login_Status_CredsModeEnabled"
                : IsAutoMode ? "Login_Status_AutoModeEnabled" : "Login_Status_ManualModeEnabled";
            ShowStatus(Loc.T(statusKey), InfoBarSeverity.Informational);
        }
    }

    private void ApplyModeVisibility()
    {
        var auto = IsAutoMode;
        var creds = IsCredsMode;
        ManualPanel.Visibility = (!auto && !creds) ? Visibility.Visible : Visibility.Collapsed;
        AutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        CredsPanel.Visibility = creds ? Visibility.Visible : Visibility.Collapsed;
        // 账密模式只用面板内的「获取令牌」按钮，下方上号/查询等动作按钮无意义，隐藏。
        ActionButtonGrid.Visibility = creds ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 自动模式（卡密解析）对接中文上游服务，英文用户用不上：英文界面隐藏「手动/自动」模式切换并强制手动模式。
    /// 其它语言保持两种模式。语言切换时（含英文↔中文）实时生效。
    /// </summary>
    private void ApplyLanguageModeAvailability()
    {
        // 自动模式（卡密）只对接中文上游，英文界面隐藏该 tab；手动 / 账密 tab 始终可用。
        var autoAvailable = !string.Equals(Loc.CurrentCode, "en", StringComparison.OrdinalIgnoreCase);
        AutoModeItem.Visibility = autoAvailable ? Visibility.Visible : Visibility.Collapsed;

        // 当前在自动模式却要隐藏它：切回手动（不弹模式切换提示）。
        if (!autoAvailable && IsAutoMode)
        {
            _suppressModeStatus = true;
            ModeSelector.SelectedItem = ManualModeItem;
            _suppressModeStatus = false;
        }

        ApplyModeVisibility();
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

    private async Task<(string AccountName, string EyaToken)> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (IsAutoMode)
        {
            var account = await ResolveLicenseAsync(cancellationToken);
            return (account.User, FormatHelper.NormalizeToken(account.Token));
        }

        var accountName = AccountNameBox.Text.Trim();
        var eyaToken = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_AccountNameRequired"));
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_EyaTokenRequired"));
        }

        return (accountName, eyaToken);
    }

    private async Task<SteamAccountData> ResolveLicenseAsync(CancellationToken cancellationToken = default)
    {
        var licenseKey = LicenseKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        var server = GetSelectedServer();
        if (_cachedAccountData is not null &&
            string.Equals(_cachedLicenseKey, licenseKey, StringComparison.Ordinal) &&
            Equals(_cachedServer, server))
        {
            return _cachedAccountData;
        }

        var account = await AppState.LicenseClient.GetAccountDataAsync(licenseKey, server, cancellationToken);
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
            ?? throw new InvalidOperationException(Loc.T("Login_Error_UpstreamServerRequired"));
    }

    /// <summary>
    /// 一键查询并保存 CS 账号状态。历史页也会调用此方法，
    /// 与旧版一致：查询过程同步更新登录页右侧的账号信息面板。
    /// </summary>
    public async Task<CsPremierScoreResult> QueryAndSaveCsStatusAsync(
        string accountName,
        string eyaToken,
        CancellationToken cancellationToken = default)
    {
        eyaToken = FormatHelper.NormalizeToken(eyaToken);
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (!tokenInfo.IsValid)
        {
            throw new InvalidOperationException($"{tokenInfo.Status}{Loc.T("Login_Error_CannotOneClickQuerySuffix")}");
        }

        var steamId = tokenInfo.SteamId
            ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdQuery"));

        UpdateAccountInfo(accountName, eyaToken);
        // 接住抓到的资料：随 CS 状态一并落盘。丢弃的话，只查询过的账号昵称/头像永远「未同步」，
        // 且下方 ApplyStoredAccountInfoProfile 会用无资料记录把面板上闪现的正确昵称头像抹回去。
        var prefetchedProfile = await UpdateAccountProfileAsync(accountName, eyaToken);
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_VerifyingAndQuerying");
        ResetAvailabilityForeground();
        AccountInfoPremierScoreText.Text = Loc.T("Login_Value_Querying");
        AccountInfoCsLevelText.Text = Loc.T("Login_Value_Querying");
        AccountInfoCooldownStatusText.Text = Loc.T("Login_Value_Querying");

        CsPremierScoreResult score;
        SteamTokenOnlineValidationResult online;
        try
        {
            score = await AppState.PremierScoreService.QueryAsync(eyaToken, steamId, cancellationToken);
            online = new SteamTokenOnlineValidationResult(true, Loc.T("Token_Result_Accepted"));
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Valid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            throw new InvalidOperationException($"{ex.Message}{Loc.T("Login_Error_CannotOneClickQuerySuffix")}", ex);
        }
        catch
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
            ResetAvailabilityForeground();
            throw;
        }

        AppState.AccountHistoryService.SaveCsAccountStatus(
            accountName,
            steamId,
            eyaToken,
            tokenInfo.ExpiresAt,
            score,
            online,
            NullIfBlank(prefetchedProfile?.PersonaName),
            NullIfBlank(prefetchedProfile?.AvatarUrl),
            NullIfBlank(prefetchedProfile?.AvatarPath));

        AppState.ReloadHistory(steamId);
        AccountInfoPremierScoreText.Text = score.DisplayText;
        AccountInfoCsLevelText.Text = score.PlayerLevelText;
        AccountInfoCooldownStatusText.Text = score.CooldownStatusText;
        ApplyStoredAccountInfoProfile(steamId);
        return score;
    }

    private async Task<string> SaveLoginHistoryAsync(
        LoginResult result,
        string eyaToken,
        SteamAccountHistoryItem? prefetchedProfile)
    {
        try
        {
            // 登录前 UpdateAccountProfileAsync 已抓过一次 persona/头像，这里直接预取传入，
            // 让 SaveLoginAsync 跳过重复抓取（新账号首登尤其明显）。
            await AppState.AccountHistoryService.SaveLoginAsync(
                result.AccountName,
                result.SteamId,
                eyaToken,
                result.ExpiresAt,
                NullIfBlank(prefetchedProfile?.PersonaName),
                NullIfBlank(prefetchedProfile?.AvatarUrl),
                NullIfBlank(prefetchedProfile?.AvatarPath));
            AppState.ReloadHistory(result.SteamId);
            ApplyStoredAccountInfoProfile(result.SteamId);
            // 兜底刷新放在保存之后：登录期间抓的预取快照已经旧了几十秒，若在登录前就刷新，
            // 新值会被上面 SaveLoginAsync 回写的旧快照覆盖（登录路径永不收敛）。
            StartBackgroundProfileRefresh(result.SteamId);
            _lastLoginHistorySaveFailed = false;
            return Loc.T("Login_Status_HistorySaved_Suffix");
        }
        catch (Exception ex)
        {
            _lastLoginHistorySaveFailed = true;
            return Loc.Tf("Login_Status_HistorySaveFailed_Suffix_Format", ex.Message);
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>右侧账号信息面板当前展示的 SteamID（由 UpdateAccountInfo 解析令牌时维护，仅 UI 线程访问）。</summary>
    private string? _accountInfoPanelSteamId;

    /// <summary>
    /// 后台重新抓取单个账号的昵称/头像并落盘，有更新时回 UI 线程重载历史列表；
    /// 仅当面板仍展示该账号时才更新面板，且只更新昵称/头像——不碰分数/等级/冷却，
    /// 避免迟到的回调覆盖「查询中…」占位或把上一个账号的资料刷进当前面板。
    /// 失败只记日志（best-effort，绝不打扰主流程）。
    /// </summary>
    private void StartBackgroundProfileRefresh(string steamId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync([steamId]);
                if (refreshed > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AppState.ReloadHistory();
                        if (!string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var account = AppState.FindHistoryAccount(steamId);
                        if (account is null)
                        {
                            return;
                        }

                        AccountInfoAvatar.DisplayName = account.AccountTitle;
                        AccountInfoAvatar.ProfilePicture = account.AvatarImage;
                        AccountInfoPersonaText.Text = account.PersonaDisplayName;
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"后台刷新账号资料失败（{steamId}）：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 同步右侧账号信息面板的头像/昵称，并返回本次已知的资料供调用方预取（避免后续 SaveLoginAsync 再抓一遍）。
    /// 命中已完整的历史记录时返回该记录；否则返回网络抓取到的预览；都拿不到返回 null。
    /// <paramref name="triggerBackgroundRefresh"/>=false 供登录路径使用：登录流程在保存历史后自行兜底刷新，
    /// 此处再触发只会让新值被登录结束时回写的旧快照覆盖。
    /// </summary>
    private async Task<SteamAccountHistoryItem?> UpdateAccountProfileAsync(
        string accountName, string eyaToken, bool triggerBackgroundRefresh = true)
    {
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return null;
        }

        var storedAccount = AppState.FindHistoryAccount(tokenInfo.SteamId);
        if (storedAccount is not null)
        {
            ApplyAccountInfoProfile(storedAccount);
            if (!string.IsNullOrWhiteSpace(storedAccount.PersonaName) &&
                (!string.IsNullOrWhiteSpace(storedAccount.AvatarPath) ||
                    !string.IsNullOrWhiteSpace(storedAccount.AvatarUrl)))
            {
                // 命中完整记录立即返回供显示，但后台仍兜底重新抓取一次：
                // 否则资料一旦落盘便永久冻结，账号在 Steam 侧改名/换头像后这里永远显示旧值。
                if (triggerBackgroundRefresh)
                {
                    StartBackgroundProfileRefresh(tokenInfo.SteamId);
                }

                return storedAccount;
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
                return profile;
            }
        }
        catch
        {
            // Profile sync is decorative; token validation and account actions should continue.
        }

        return storedAccount;
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
            // 留空而非占位文案：PersonPicture 会据 DisplayName 取首字母（英文 "Not set"→"NS"），
            // 空串才回退到默认人像剪影——中文 "未填写" 因 CJK 不生成首字母恰好显示剪影，这里统一成空保持一致。
            AccountInfoAvatar.DisplayName = string.Empty;
            AccountInfoPersonaText.Text = Loc.T("Login_Value_PersonaNotSynced");
            AccountInfoPremierScoreText.Text = Loc.T("Login_Value_NotQueried");
            AccountInfoCsLevelText.Text = Loc.T("Login_Value_NotQueried");
            AccountInfoCooldownStatusText.Text = Loc.T("Login_Value_NotQueried");
            return;
        }

        AccountInfoAvatar.DisplayName = account.AccountTitle;
        AccountInfoAvatar.ProfilePicture = account.AvatarImage;
        AccountInfoPersonaText.Text = account.PersonaDisplayName;
        AccountInfoPremierScoreText.Text = account.CompetitiveScoreText;
        AccountInfoCsLevelText.Text = account.CsPlayerLevelText;
        AccountInfoCooldownStatusText.Text = account.CooldownStatusText;
    }

    // actionName 是稳定的本地化键（Login_Action_*），不是显示文案：既用于和具体动作比较，
    // 也用 Loc.T(actionName) 取当前语言的动作名拼进报错，避免用界面文本做控制流（多语言下会失配）。
    private void EnsureTokenValidForAction(string eyaToken, string actionName)
    {
        var info = AppState.JwtTokenService.Inspect(eyaToken);
        if (info.IsValid)
        {
            return;
        }

        // 用结构化的过期时间判定「已过期」，而非比较本地化后的状态文案。
        var isExpired = info.ExpiresAt is { } expiry && expiry <= DateTimeOffset.Now;
        if (isExpired && actionName == "Login_Action_ClearWorkshop")
        {
            throw new InvalidOperationException(Loc.T("Login_Error_TokenExpiredCannotClearWorkshop"));
        }

        throw new InvalidOperationException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", info.Status, Loc.T(actionName)));
    }

    private void UpdateAccountInfoFromCurrentInputs()
    {
        if (AccountInfoUserText is null)
        {
            return;
        }

        if (IsCredsMode)
        {
            // 账密模式还没有令牌，右侧账号信息面板清空。
            UpdateAccountInfo(null, null);
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

    /// <summary>
    /// 可用状态恢复默认前景色：未验证/验证中等占位与「未填写/未解析」等字段保持同色，
    /// 仅验证结果（有效/无效）才用成功/失败色。
    /// </summary>
    private void ResetAvailabilityForeground() =>
        AccountInfoAvailabilityText.ClearValue(TextBlock.ForegroundProperty);

    private void UpdateAccountInfo(string? userName, string? token)
    {
        AccountInfoUserText.Text = string.IsNullOrWhiteSpace(userName) ? Loc.T("Login_Value_NotFilled") : userName;
        ApplyAccountInfoProfile(null);
        // 用户名为空时给空串（显示默认人像剪影），不给占位文案，避免 PersonPicture 取首字母得到 "NS" 之类。
        AccountInfoAvatar.DisplayName = string.IsNullOrWhiteSpace(userName) ? string.Empty : userName;

        if (string.IsNullOrWhiteSpace(token))
        {
            _accountInfoPanelSteamId = null;
            AccountInfoSteamIdText.Text = Loc.T("Login_Value_NotResolved");
            AccountInfoExpiresText.Text = Loc.T("Login_Value_NotResolved");
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
            ResetAvailabilityForeground();
            return;
        }

        var info = AppState.JwtTokenService.Inspect(token);
        _accountInfoPanelSteamId = string.IsNullOrWhiteSpace(info.SteamId) ? null : info.SteamId;
        AccountInfoSteamIdText.Text = string.IsNullOrWhiteSpace(info.SteamId) ? Loc.T("Login_Value_NotResolved") : info.SteamId;
        AccountInfoExpiresText.Text = info.ExpiresAt.HasValue
            ? info.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : Loc.T("Login_Value_NotResolved");
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
        ResetAvailabilityForeground();

        if (!string.IsNullOrWhiteSpace(info.SteamId))
        {
            ApplyStoredAccountInfoProfile(info.SteamId);
        }
    }

    private async Task EnsureTokenAcceptedBySteamAsync(
        string eyaToken,
        string actionName,
        CancellationToken cancellationToken = default)
    {
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
        ResetAvailabilityForeground();

        SteamTokenOnlineValidationResult online;
        try
        {
            // ValidateAsync 只把「令牌被 Steam 拒绝」转成 IsValid=false，网络/CM 不可达（含
            // TimeoutException）会向上抛——上号本身只写 VDF+启动 Steam，不依赖本机连得上 CM，
            // 故网络故障不应阻断上号，仅给中性提示后继续。
            online = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"在线验证令牌时无法连接 Steam，已跳过在线验证继续{Loc.T(actionName)}：{ex.Message}");
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerifiedOffline");
            ResetAvailabilityForeground();
            ShowStatus(Loc.Tf("Login_Status_SkippedOnlineValidation_Format", Loc.T(actionName)), InfoBarSeverity.Warning);
            return;
        }

        AccountInfoAvailabilityText.Text = online.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid");
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
            online.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);

        if (online.IsValid)
        {
            return;
        }

        // 走到这里是令牌确实被 Steam 拒绝（IsTokenFailure 路径），维持红色并阻断后续动作。
        throw new InvalidOperationException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", online.Status, Loc.T(actionName)));
    }

    private async Task<SteamTokenOnlineValidationResult> ValidateTokenOnlineAsync(
        string eyaToken,
        CancellationToken cancellationToken = default)
    {
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
        ResetAvailabilityForeground();

        try
        {
            var result = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
            AccountInfoAvailabilityText.Text = result.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
                result.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消：向上抛给 ResolveLicenseInteractiveAsync 统一按「已取消」处理。
            throw;
        }
        catch (Exception ex)
        {
            var result = new SteamTokenOnlineValidationResult(false, Loc.Tf("Login_Status_OnlineValidationFailed_Format", ex.Message));
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            return result;
        }
    }
}
