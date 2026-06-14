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
            LanguageComboBox.SelectedItem = FindByTag(LanguageComboBox, Loc.CurrentCode);
            ThemeComboBox.SelectedItem = FindByTag(ThemeComboBox, AppState.SettingsService.Load().Theme) ?? ThemeComboBox.Items[0];
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
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

    /// <summary>手动更改上号使用的 Steam 安装目录（多 Steam 时指定要用哪一个）。选择器+校验+持久化都在协调器里。</summary>
    private async void ChangeSteamPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SteamPathCoordinator.PickAndPersistManuallyAsync())
        {
            UpdateSteamPathText();
            AppState.ShowStatus(Loc.T("Settings_SteamPath_Changed"), InfoBarSeverity.Success);
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
