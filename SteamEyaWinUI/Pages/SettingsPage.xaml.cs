using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
    private bool _fontItemsBuilt;

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
        BuildFontItems();
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

            if (FontComboBox.Items.FirstOrDefault() is ComboBoxItem defaultFontItem)
            {
                defaultFontItem.Content = Loc.T("Settings_Font_System");
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

            // 来源账号候选/选中文本用 Settings_Cs2Sync_SourceItem_Format 拼装，跟随语言重建。
            RefreshCs2SyncSources();
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

    private void BuildFontItems()
    {
        if (_fontItemsBuilt)
        {
            return;
        }

        ReloadFontItems(AppState.SettingsService.Load().FontFamily);
    }

    private int ReloadFontItems(string? selectedSource)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            FontComboBox.Items.Clear();
            FontComboBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("Settings_Font_System"),
                Tag = ""
            });

            var fonts = AppFontCatalog.Load();
            foreach (var font in fonts)
            {
                FontComboBox.Items.Add(new ComboBoxItem
                {
                    Content = font.DisplayName,
                    Tag = font.Source,
                    FontFamily = new FontFamily(font.Source)
                });
            }

            _fontItemsBuilt = true;
            FontComboBox.SelectedItem = FindByTag(FontComboBox, selectedSource ?? "") ?? FontComboBox.Items[0];
            return fonts.Count;
        }
        finally
        {
            _syncing = wasSyncing;
        }
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
            FontComboBox.SelectedItem = FindByTag(FontComboBox, settings.FontFamily ?? "") ?? FontComboBox.Items[0];
            GlassEffectToggle.IsOn = settings.GlassEffectEnabled;
            BackgroundOpacitySlider.Value = settings.BackgroundOpacity;
            BackgroundOpacitySlider.IsEnabled = settings.GlassEffectEnabled;
            UpdateBackgroundOpacityText(settings.BackgroundOpacity);
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

    private void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (FontComboBox.SelectedItem as ComboBoxItem)?.Tag is not string familyName)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.FontFamily = string.IsNullOrWhiteSpace(familyName) ? null : familyName;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyFontFamily(settings.FontFamily);
    }

    private void OpenFontFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppFontCatalog.FontFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开字体目录失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Font_OpenFolderFail"), InfoBarSeverity.Error);
        }
    }

    private void RefreshFontListButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        var selectedSource = settings.FontFamily;
        var count = ReloadFontItems(selectedSource);

        if (!string.IsNullOrWhiteSpace(selectedSource) && FindByTag(FontComboBox, selectedSource) is null)
        {
            settings.FontFamily = null;
            AppState.SettingsService.Save(settings);
            MainWindow.Instance?.ApplyFontFamily(null);
        }

        AppState.ShowStatus(Loc.Tf("Settings_Font_Refreshed_Format", count), InfoBarSeverity.Success);
    }

    private void GlassEffectToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.GlassEffectEnabled = GlassEffectToggle.IsOn;
        settings.BackgroundOpacity = (int)Math.Round(BackgroundOpacitySlider.Value);
        AppState.SettingsService.Save(settings);
        BackgroundOpacitySlider.IsEnabled = settings.GlassEffectEnabled;
        MainWindow.Instance?.ApplyGlassAppearance(
            settings.GlassEffectEnabled,
            settings.BackgroundOpacity);
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var opacity = (int)Math.Round(e.NewValue);
        UpdateBackgroundOpacityText(opacity);
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.BackgroundOpacity = opacity;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyGlassSurfaceOpacity(opacity);
    }

    private void UpdateBackgroundOpacityText(int opacity)
    {
        if (BackgroundOpacityText is not null)
        {
            BackgroundOpacityText.Text = $"{opacity}%";
        }
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

    /// <summary>来源账号下拉候选：展示文本（昵称（Steam64））+ 搜索文本（昵称/账号名/Steam64/历史备注）。</summary>
    private sealed record Cs2SourceOption(string SteamId64, string Display, string SearchText);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // 已保存的来源账号 SteamID64 的本地镜像，避免各处反复 Load 设置来判断“选中了谁”。
    private string? _cs2SourceSteamId;

    // 刷新代数：语言切换 / 刷新按钮连点 / 导航竞态下并发多轮扫描时，只让最后发起的一轮落地。
    private int _cs2RefreshGen;

    // 搜索框（内部 TextBox）是否持有焦点：刷新完成时正在输入则不动文本/候选，避免打断用户。
    private bool _cs2SourceBoxFocused;

    private async void RefreshCs2SyncSources()
    {
        // userdata 目录解析 + 扫描来源账号 + 离线名称解析都放后台线程：大 userdata / 慢盘 /
        // 数 MB 的 localconfig.vdf 都不卡设置页导航。
        // userdata 路径与「立即推送」一致走 ResolvePathsOrThrow（带自动探测回退），
        // 修掉“未持久化 Steam 路径时下拉恒空、但立即推送却能工作”的不一致。
        var gen = ++_cs2RefreshGen;
        var (sources, names) = await Task.Run(() =>
        {
            try
            {
                var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                var scanned = AppState.Cs2CloudService.EnumerateSources(paths.UserdataPath);
                return (scanned, SteamAccountNameService.BuildOfflineNames(paths, scanned));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"扫描 CS2 设置来源账号失败：{ex.Message}");
                return ((IReadOnlyList<Cs2SettingsSource>)Array.Empty<Cs2SettingsSource>(),
                    (IReadOnlyDictionary<string, OfflineAccountName>)new Dictionary<string, OfflineAccountName>());
            }
        });

        if (gen != _cs2RefreshGen)
        {
            return;
        }

        _syncing = true;
        try
        {
            _cs2SourceOptions = BuildCs2SourceOptions(sources, names);

            var settings = AppState.SettingsService.Load();
            Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
            _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

            // 正在输入时不动文本/候选：下一次击键会用新数据重新过滤，失焦时恢复规范文本。
            if (!_cs2SourceBoxFocused)
            {
                Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
                Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>拼装候选并排序：有名字的按名字排前面，只剩 Steam64 的按数字排后面。</summary>
    private static List<Cs2SourceOption> BuildCs2SourceOptions(
        IReadOnlyList<Cs2SettingsSource> sources,
        IReadOnlyDictionary<string, OfflineAccountName> names)
    {
        var options = new List<Cs2SourceOption>(sources.Count);
        foreach (var source in sources)
        {
            // 显示名优先级：应用历史（含在线刷新过的昵称）> 本机 Steam 文件的离线解析。
            var history = AppState.HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, source.SteamId64, StringComparison.OrdinalIgnoreCase));
            names.TryGetValue(source.SteamId64, out var offline);

            var persona = FirstNonEmpty(history?.PersonaName, offline?.PersonaName);
            var accountName = FirstNonEmpty(history?.AccountName, offline?.AccountName);
            var name = persona ?? accountName;
            // 有的数据源会把 Steam64 本身存成名字，显示成「X（X）」纯属噪音，按无名处理。
            if (string.Equals(name, source.SteamId64, StringComparison.OrdinalIgnoreCase))
            {
                name = null;
            }

            var display = name is null
                ? source.SteamId64
                : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, source.SteamId64);

            // 搜索面覆盖昵称、登录账号名、Steam64 和历史备注——展示文本只放昵称，避免下拉太长。
            var searchText = string.Join(' ',
                new[] { persona, accountName, source.SteamId64, history?.Note }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            options.Add(new Cs2SourceOption(source.SteamId64, display, searchText));
        }

        return options
            .OrderBy(option => option.Display == option.SteamId64 ? 1 : 0)
            .ThenBy(option => option.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    /// <summary>当前已保存来源的展示文本；来源目录已消失时退回显示原始 Steam64，未选择时为空。</summary>
    private string Cs2SourceSelectedDisplay()
    {
        if (string.IsNullOrWhiteSpace(_cs2SourceSteamId))
        {
            return string.Empty;
        }

        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase));
        return option?.Display ?? _cs2SourceSteamId;
    }

    private List<string> FilterCs2SourceDisplays(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        return _cs2SourceOptions
            .Where(option => trimmed.Length == 0 ||
                option.SearchText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Display)
            .ToList();
    }

    /// <summary>把候选设为当前来源：规范化文本并持久化（重复选择同一账号时不重复写盘）。</summary>
    private void CommitCs2Source(string display)
    {
        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.Display, display, StringComparison.Ordinal));
        if (option is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = option.Display;
        }
        finally
        {
            _syncing = false;
        }

        if (string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cs2SourceSteamId = option.SteamId64;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = option.SteamId64;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncSourceBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只响应用户敲键；程序赋值/选中回填触发的 TextChanged 不重开候选。
        if (_syncing || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = FilterCs2SourceDisplays(sender.Text);
    }

    private void Cs2SyncSourceBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            CommitCs2Source(chosen);
            return;
        }

        // 直接回车：文本恰好等于某候选、或非空搜索词过滤后只剩一个候选，则视为选中它。
        // 空文本回车不自动选中（单账号机器上会“无输入即选择”），只展开全部候选。
        var exact = _cs2SourceOptions.FirstOrDefault(option =>
            string.Equals(option.Display, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            CommitCs2Source(exact.Display);
            return;
        }

        var matches = FilterCs2SourceDisplays(args.QueryText);
        if (matches.Count == 1 && !string.IsNullOrWhiteSpace(args.QueryText))
        {
            CommitCs2Source(matches[0]);
            return;
        }

        sender.ItemsSource = matches;
    }

    private void Cs2SyncSourceBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = true;

        // 获得焦点即展开全部候选，保留旧 ComboBox「点开就能挑」的体验。
        Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
        if (_cs2SourceOptions.Count > 0)
        {
            Cs2SyncSourceBox.IsSuggestionListOpen = true;
        }
    }

    private void Cs2SyncSourceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = false;

        if (_syncing)
        {
            return;
        }

        // 提交只走 QuerySubmitted（回车/点选候选）。失焦一律回退为已保存选择的展示文本，
        // 与旧 ComboBox 的轻取消语义一致：仅方向键预览过的候选或半截搜索词都不算选择，
        // 否则“瞄一眼就点走”会把高亮项静默写进设置，后续推送就推错账号。
        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
        }
        finally
        {
            _syncing = false;
        }
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
