using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class CachedAccountsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<CachedSteamLoginAccount> _viewItems = [];
    private IReadOnlyList<CachedSteamLoginAccount> _sourceItems = [];
    private bool _isDialogFlowActive;

    /// <summary>
    /// 批量勾选集（账号 CacheKey）。与 ListView 的单选（详情焦点）解耦：勾选卡片左上角复选框进入此集，
    /// 驱动卡片黑框+对勾与底部批量操作栏。按键存储以便跨列表重建（换新实例）保留勾选。与历史账号页一致。
    /// </summary>
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

    public CachedAccountsPage()
    {
        InitializeComponent();
        CachedAccountList.ItemsSource = _viewItems;
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        // 登录触发的后台资料刷新落盘后自动重载（事件来自后台线程，回 UI 线程执行）；
        // Reload(GetSelectedKey()) 按键恢复选中，批量勾选按 _checkedKeys 跨重建保留。
        AppState.CachedLoginAccountsRefreshed += () =>
            _dispatcherQueue.TryEnqueue(() => Reload(GetSelectedKey()));
        Loc.LanguageChanged += OnLanguageChanged;
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；命令式文本（概要/空状态/批量栏/详情面板）重跑对应方法即可换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            RebuildView(GetSelectedKey());
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload(GetSelectedKey());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 悬停时被导航走，PointerExited 可能不触发；清掉残留悬停态，避免回来后空心勾选圈残留。
        foreach (var account in _sourceItems)
        {
            account.IsPointerOver = false;
        }
    }

    private void Reload(string? selectKey = null)
    {
        _sourceItems = AppState.LoginService.GetCachedLoginAccounts();
        RebuildView(selectKey);
    }

    private void RebuildView(string? selectKey)
    {
        var source = _sourceItems;
        var filter = CachedSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(account => Matches(account, filter)).ToList();

        // 批量勾选集按 CacheKey 跨重建保留：先剔除已不存在的账号，再把勾选状态套用到（可能是新的）实例。
        var liveKeys = source.Select(account => account.CacheKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.IntersectWith(liveKeys);
        foreach (var account in source)
        {
            account.IsSelected = _checkedKeys.Contains(account.CacheKey);
        }

        // 记住当前单选（详情焦点）的账号键，重建后恢复——刷新等延迟操作不应丢失当前查看的账号。
        var activeKey = !string.IsNullOrWhiteSpace(selectKey)
            ? selectKey
            : CachedAccountList.SelectedItem is CachedSteamLoginAccount current ? current.CacheKey : null;

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var active = activeKey is null
            ? null
            : _viewItems.FirstOrDefault(account =>
                string.Equals(account.CacheKey, activeKey, StringComparison.OrdinalIgnoreCase));
        CachedAccountList.SelectedItem = active ?? _viewItems.FirstOrDefault();

        var hasAny = source.Count > 0;
        CachedEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CachedEmptyText.Text = hasAny ? Loc.T("Cached_Empty_NoMatch") : Loc.T("Cached_Empty_None");
        CachedEmptyHintText.Text = hasAny
            ? Loc.T("Cached_Empty_NoMatch_Hint")
            : Loc.T("Cached_Empty_None_Hint");
        CachedSummaryText.Text = hasAny
            ? Loc.Tf("Cached_Summary_Count_Format", source.Count)
            : Loc.T("Cached_Summary_Empty");

        UpdateBatchBar();
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private static bool Matches(CachedSteamLoginAccount account, string filter)
    {
        return Contains(account.AccountName, filter) ||
            Contains(account.PersonaName, filter) ||
            Contains(account.SteamId, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrEmpty(value) &&
            value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void CachedSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildView(GetSelectedKey());
        }
    }

    private async void RefreshCachedButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        AppState.ShowStatus(Loc.T("Cached_Refresh_Progress"), InfoBarSeverity.Informational);

        try
        {
            var refreshed = await AppState.LoginService.RefreshCachedLoginProfilesAsync(_sourceItems);
            Reload(GetSelectedKey());
            if (refreshed > 0)
            {
                AppState.ShowStatus(Loc.Tf("Cached_Refresh_Success_Format", refreshed), InfoBarSeverity.Success);
            }
            else
            {
                // 一个都没同步成（断网/被限流/无账号）不能弹绿色成功条装作没事。
                AppState.ShowStatus(Loc.T("Cached_Refresh_None"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Reload(GetSelectedKey());
            AppState.ShowStatus(Loc.Tf("Cached_Refresh_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void CachedAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private async void RestoreSelectedCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            AppState.ShowStatus(Loc.T("Cached_Restore_NoSelection"), InfoBarSeverity.Error);
            return;
        }

        // 首启自动检测并持久化 Steam 路径；失效则重测；都不行才弹框让用户选。用户取消即中止恢复。
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            AppState.ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        AppState.SetBusy(true);
        AppState.ShowStatus(Loc.Tf("Cached_Restore_Progress_Format", account.AccountTitle), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var restored = await Task.Run(() => AppState.LoginService.RestoreCachedLogin(account, progress));
            Reload(restored.CacheKey);
            AppState.ShowStatus(
                Loc.Tf("Cached_Restore_Success_Format", restored.AccountName, restored.SteamId),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("恢复缓存账号失败。", ex);
            AppState.ShowStatus(Loc.Tf("Cached_Restore_Fail_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void ClearCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (_sourceItems.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Cached_Clear_None"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("Cached_Clear_Dialog_Title"),
            Content = Loc.Tf("Cached_Clear_Dialog_Content_Format", _sourceItems.Count),
            PrimaryButtonText = Loc.T("Common_Clear"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _checkedKeys.Clear();
            var cleared = AppState.LoginService.ClearCachedLoginAccounts();
            Reload();
            AppState.ShowStatus(Loc.Tf("Cached_Clear_Success_Format", cleared), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Cached_Clear_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    // ---------- 卡片悬停 / 左上角勾选（与历史账号页一致） ----------

    private static CachedSteamLoginAccount? CardItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CachedSteamLoginAccount;

    private void CachedCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = true;
        }
    }

    private void CachedCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = false;
        }
    }

    private void CachedCardCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        var key = account.CacheKey;
        if (account.IsSelected)
        {
            account.IsSelected = false;
            _checkedKeys.Remove(key);
        }
        else
        {
            account.IsSelected = true;
            _checkedKeys.Add(key);
        }

        UpdateBatchBar();
        UpdateControlsEnabled();
    }

    private async void CachedCardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            await DeleteAccountsWithConfirmAsync(new[] { account });
        }
    }

    // ---------- 底部批量操作栏（勾选任意卡片后浮现，操作针对全部已勾选账号） ----------

    // 只作用于当前可见（已过滤）列表：避免搜索过滤下对看不见的勾选项执行批量删除。
    // 被过滤隐藏的勾选项仍保留在 _checkedKeys，清空搜索后会重新出现并计入。
    private List<CachedSteamLoginAccount> GetCheckedAccounts() =>
        _viewItems.Where(account => _checkedKeys.Contains(account.CacheKey)).ToList();

    private void UpdateBatchBar()
    {
        var count = GetCheckedAccounts().Count;
        BatchActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = Loc.Tf("Common_Selected_Format", count);
    }

    private void ClearCheckedSelection()
    {
        _checkedKeys.Clear();
        foreach (var account in _sourceItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
        UpdateControlsEnabled();
    }

    private void BatchClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCheckedSelection();
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteAccountsWithConfirmAsync(GetCheckedAccounts());
    }

    private async Task DeleteAccountsWithConfirmAsync(IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Cached_Delete_NoSelection"), InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join("、", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? Loc.Tf("Cached_Delete_Dialog_Many_Format", nameText, accounts.Count)
            : Loc.Tf("Cached_Delete_Dialog_Few_Format", nameText, accounts.Count);

        var dialog = new ContentDialog
        {
            Title = Loc.T("Cached_Delete_Dialog_Title"),
            Content = summary,
            PrimaryButtonText = Loc.T("Common_Delete"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // 删除前把这些键移出批量选择集，避免重建后残留在已选状态。
            foreach (var account in accounts)
            {
                _checkedKeys.Remove(account.CacheKey);
            }

            var removed = AppState.LoginService.DeleteCachedLoginAccounts(accounts);
            Reload();
            AppState.ShowStatus(Loc.Tf("Cached_Delete_Success_Format", removed), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Cached_Delete_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private string? GetSelectedKey()
    {
        return CachedAccountList.SelectedItem is CachedSteamLoginAccount account
            ? account.CacheKey
            : null;
    }

    private void UpdateDetail()
    {
        if (CachedDetailAccountNameText is null)
        {
            return;
        }

        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            CachedDetailAvatar.ProfilePicture = null;
            CachedDetailAvatar.DisplayName = Loc.T("Cached_Detail_Avatar_None");
            CachedDetailAccountNameText.Text = Loc.T("Cached_Detail_NoSelection");
            CachedDetailPersonaText.Text = Loc.T("Cached_Detail_PersonaUnsynced");
            CachedDetailSteamIdText.Text = Loc.T("Cached_Detail_SteamIdMissing");
            CachedDetailTimeText.Text = Loc.T("Cached_Detail_TimeNone");
            return;
        }

        CachedDetailAvatar.DisplayName = account.AccountTitle;
        CachedDetailAvatar.ProfilePicture = account.AvatarImage;
        CachedDetailAccountNameText.Text = account.AccountTitle;
        CachedDetailPersonaText.Text = account.PersonaDisplayName;
        CachedDetailSteamIdText.Text = account.SteamIdDisplay;
        CachedDetailTimeText.Text = Loc.Tf("Cached_Detail_Time_Format", account.CachedAtText);
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        CachedAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        CachedSearchBox.IsEnabled = !isBusy;
        RefreshCachedButton.IsEnabled = !isBusy;
        ClearCachedButton.IsEnabled = !isBusy && !_isDialogFlowActive && _sourceItems.Count > 0;
        RestoreSelectedCachedButton.IsEnabled = !isBusy && CachedAccountList.SelectedItem is not null;
        BatchClearButton.IsEnabled = !isBusy;
        BatchDeleteButton.IsEnabled = !isBusy && !_isDialogFlowActive;
    }
}
