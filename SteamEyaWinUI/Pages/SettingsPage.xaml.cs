using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // 代码设置 ComboBox.SelectedItem 会触发 SelectionChanged，置位以区分“用户选择”与“初始同步”，避免回写/重复应用。
    private bool _syncing;
    private bool _languageItemsBuilt;

    public SettingsPage()
    {
        InitializeComponent();

        // 语言切换后，让本页所有 {x:Bind Strings.Get(...), Mode=OneWay} 重新求值（主题项文本等）。
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BuildLanguageItems();
        SyncFromSettings();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));

            // 主题项 Content 经 x:Bind 已换语言，但 ComboBox 关闭态的“选中框”显示的是选中项内容的快照，
            // 不会自动重读——把 SelectedItem 重置一轮强制刷新。同一 UI 回调内同步完成，中间空态不渲染，无闪烁。
            // （语言下拉的项是各语言自称名，不随界面语言变，故无需处理。）
            var theme = ThemeComboBox.SelectedItem;
            if (theme is not null)
            {
                _syncing = true;
                ThemeComboBox.SelectedItem = null;
                ThemeComboBox.SelectedItem = theme;
                _syncing = false;
            }

            var updateProxy = UpdateProxyComboBox.SelectedItem;
            if (updateProxy is not null)
            {
                _syncing = true;
                UpdateProxyComboBox.SelectedItem = null;
                UpdateProxyComboBox.SelectedItem = updateProxy;
                _syncing = false;
            }

            // 未设置时 SteamPathText 显示的是本地化占位文案，需随语言刷新（已设置时是中性路径，刷新无副作用）。
            UpdateSteamPathText();
        });
    }

    /// <summary>按已加载的语言包动态生成语言下拉项（只建一次）。语言自称名不随界面语言变化。</summary>
    private void BuildLanguageItems()
    {
        if (_languageItemsBuilt)
        {
            return;
        }

        foreach (var pack in Loc.AvailablePacks)
        {
            LanguageComboBox.Items.Add(new ComboBoxItem { Content = pack.Name, Tag = pack.Code });
        }

        _languageItemsBuilt = true;
    }

    /// <summary>按当前语言与已保存主题选中对应下拉项；过程中屏蔽 SelectionChanged 处理。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            var settings = AppState.SettingsService.Load();
            LanguageComboBox.SelectedItem = FindByTag(LanguageComboBox, Loc.CurrentCode);
            ThemeComboBox.SelectedItem = FindByTag(ThemeComboBox, settings.Theme) ?? ThemeComboBox.Items[0];
            UpdateProxyComboBox.SelectedItem = FindByTag(UpdateProxyComboBox, settings.UpdateProxySite) ?? UpdateProxyComboBox.Items[0];
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
        RefreshCs2SyncSources();
    }

    /// <summary>显示当前持久化的 Steam 安装目录；未设置时显示占位文案（启动会自动检测）。</summary>
    private void UpdateSteamPathText()
    {
        SteamPathText.Text = SteamPathCoordinator.GetPersistedInstallPath() ?? Loc.T("Settings_SteamPath_NotSet");
    }

    private static ComboBoxItem? FindByTag(ComboBox combo, string tag) =>
        combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string value &&
                string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag is not string code)
        {
            return;
        }

        Loc.SetLanguage(code);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag is not string theme)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Theme = theme;
        AppState.SettingsService.Save(settings);

        MainWindow.Instance?.ApplyTheme(theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        });
    }

    private void UpdateProxyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (UpdateProxyComboBox.SelectedItem as ComboBoxItem)?.Tag is not string proxyCode)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.UpdateProxySite = proxyCode;
        AppState.SettingsService.Save(settings);
        AppState.UpdateService.SetProxySite(proxyCode);
    }

    private async void UpdateProxyLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var proxyCode = (UpdateProxyComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(proxyCode))
        {
            return;
        }

        UpdateProxyLatencyButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_UpdateProxy_Testing"), InfoBarSeverity.Informational);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var elapsed = await AppState.UpdateService.ProbeLatencyAsync(proxyCode, cts.Token);
            AppState.ShowStatus(
                Loc.Tf("Settings_UpdateProxy_LatencyResult_Format", Math.Round(elapsed.TotalMilliseconds)),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("Settings_UpdateProxy_LatencyTimeout"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Settings_UpdateProxy_LatencyFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            UpdateProxyLatencyButton.IsEnabled = true;
        }
    }

    /// <summary>手动更改上号使用的 Steam 安装目录（多 Steam 时指定要用哪一个）。选择器+校验+持久化都在协调器里。</summary>
    private async void ChangeSteamPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SteamPathCoordinator.PickAndPersistManuallyAsync())
        {
            UpdateSteamPathText();
            AppState.ShowStatus(Loc.T("Settings_SteamPath_Changed"), InfoBarSeverity.Success);
        }
    }

    // ---------- CS2 设置同步（issue #10）：来源账号 + 登录时强推 + 立即推送 ----------

    private async void RefreshCs2SyncSources()
    {
        // userdata 目录解析 + 扫描来源账号都放后台线程：大 userdata / 慢盘时不再卡住设置页导航。
        // userdata 路径与「立即推送」一致走 ResolvePathsOrThrow（带自动探测回退），
        // 修掉“未持久化 Steam 路径时下拉恒空、但立即推送却能工作”的不一致。
        var sources = await Task.Run(() =>
        {
            try
            {
                var userdataPath = SteamPathCoordinator.ResolvePathsOrThrow().UserdataPath;
                return AppState.Cs2CloudService.EnumerateSources(userdataPath);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"扫描 CS2 设置来源账号失败：{ex.Message}");
                return (IReadOnlyList<Cs2SettingsSource>)Array.Empty<Cs2SettingsSource>();
            }
        });

        _syncing = true;
        try
        {
            Cs2SyncSourceCombo.Items.Clear();
            foreach (var source in sources)
            {
                Cs2SyncSourceCombo.Items.Add(new ComboBoxItem
                {
                    Content = DescribeAccount(source.SteamId64),
                    Tag = source.SteamId64
                });
            }

            var settings = AppState.SettingsService.Load();
            Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
            Cs2SyncSourceCombo.SelectedItem = FindByTag(Cs2SyncSourceCombo, settings.Cs2SyncSourceSteamId ?? string.Empty);
        }
        finally
        {
            _syncing = false;
        }
    }

    // 来源账号显示名：优先用历史账号里的昵称/账户名，找不到就显示 SteamID64。
    private static string DescribeAccount(string steamId64)
    {
        var account = AppState.HistoryAccounts.FirstOrDefault(item =>
            string.Equals(item.SteamId, steamId64, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return steamId64;
        }

        var name = !string.IsNullOrWhiteSpace(account.PersonaName) ? account.PersonaName : account.AccountName;
        return string.IsNullOrWhiteSpace(name)
            ? steamId64
            : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, steamId64);
    }

    private void Cs2SyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncOnLogin = Cs2SyncToggle.IsOn;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var steamId = (Cs2SyncSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = string.IsNullOrWhiteSpace(steamId) ? null : steamId;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCs2SyncSources();
    }

    private async void Cs2SyncPushNowButton_Click(object sender, RoutedEventArgs e)
    {
        var source = AppState.SettingsService.Load().Cs2SyncSourceSteamId;
        if (string.IsNullOrWhiteSpace(source))
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Error_NoSourceSelected"), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
        // 推送期间禁用按钮，避免重复点击排队多次串行推送。
        Cs2SyncPushNowButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                    return AppState.Cs2CloudService.PushSourceNow(paths, source);
                }
                catch (Exception ex)
                {
                    return new Cs2CloudPushResult(false, 0, ex.Message);
                }
            });

            var severity = !result.Ok
                ? InfoBarSeverity.Error
                : result.AccountCloudDisabled ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            AppState.ShowStatus(Cs2CloudService.DescribeResult(result), severity);
        }
        finally
        {
            Cs2SyncPushNowButton.IsEnabled = true;
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppState.SettingsService.AppFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            // explorer.exe 接受目录路径作参数直接打开资源管理器；比 ShellExecute 文件夹更稳。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_OpenFail"), InfoBarSeverity.Error);
        }
    }
}
