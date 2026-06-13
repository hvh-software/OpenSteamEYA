using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class CachedAccountsPage : Page
{
    private readonly ObservableCollection<CachedSteamLoginAccount> _viewItems = [];
    private IReadOnlyList<CachedSteamLoginAccount> _sourceItems = [];
    private bool _isDialogFlowActive;

    public CachedAccountsPage()
    {
        InitializeComponent();
        CachedAccountList.ItemsSource = _viewItems;
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        Reload();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload(GetSelectedKey());
    }

    private void Reload(string? selectKey = null)
    {
        _sourceItems = AppState.LoginService.GetCachedLoginAccounts();
        RebuildView(selectKey);
    }

    private void RebuildView(string? selectKey)
    {
        var filter = CachedSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _sourceItems
            : _sourceItems.Where(account => Matches(account, filter)).ToList();

        var selectedKeys = CachedAccountList.SelectedItems
            .OfType<CachedSteamLoginAccount>()
            .Select(account => account.CacheKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectKey))
        {
            selectedKeys.Add(selectKey);
        }

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var toSelect = _viewItems
            .Where(account => selectedKeys.Contains(account.CacheKey))
            .ToList();
        if (toSelect.Count <= 1)
        {
            CachedAccountList.SelectedItem = toSelect.Count == 1 ? toSelect[0] : _viewItems.FirstOrDefault();
        }
        else
        {
            foreach (var account in toSelect)
            {
                CachedAccountList.SelectedItems.Add(account);
            }
        }

        var hasAny = _sourceItems.Count > 0;
        CachedEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CachedEmptyText.Text = hasAny ? "没有匹配的缓存账号" : "暂无缓存账号";
        CachedEmptyHintText.Text = hasAny
            ? "换个关键词试试，或清空搜索框。"
            : "先正常登录 Steam，再使用 EYA 登录，原 Steam 账号会自动缓存。";
        CachedSummaryText.Text = hasAny
            ? $"共 {_sourceItems.Count} 个缓存账号，使用 EYA 登录前检测到的原 Steam 登录账号会记录在这里。"
            : "使用 EYA 登录前检测到的原 Steam 登录账号会记录在这里。";

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
        AppState.ShowStatus("正在刷新缓存账号资料...", InfoBarSeverity.Informational);

        try
        {
            var refreshed = await AppState.LoginService.RefreshCachedLoginProfilesAsync(_sourceItems);
            Reload(GetSelectedKey());
            AppState.ShowStatus($"缓存账号已刷新，已同步 {refreshed} 个头像/资料。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Reload(GetSelectedKey());
            AppState.ShowStatus($"刷新缓存账号资料失败：{ex.Message}", InfoBarSeverity.Error);
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
            AppState.ShowStatus("请先选择要恢复的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        AppState.SetBusy(true);
        AppState.ShowStatus($"正在恢复缓存账号 {account.AccountTitle}...", InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var restored = await Task.Run(() => AppState.LoginService.RestoreCachedLogin(account, progress));
            Reload(restored.CacheKey);
            AppState.ShowStatus(
                $"已请求恢复缓存账号：{restored.AccountName}（SteamID: {restored.SteamId}）。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("恢复缓存账号失败。", ex);
            AppState.ShowStatus($"{ex.Message}（诊断日志：{AppLog.LogFilePath}）", InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void DeleteCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var accounts = GetSelectedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus("请先选择要删除的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join("、", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? $"将删除 {nameText} 等 {accounts.Count} 个缓存账号，仅移除本地缓存记录与头像。"
            : $"将删除 {nameText}（共 {accounts.Count} 个），仅移除本地缓存记录与头像。";

        var dialog = new ContentDialog
        {
            Title = "删除缓存账号",
            Content = summary,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
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

            var removed = AppState.LoginService.DeleteCachedLoginAccounts(accounts);
            Reload();
            AppState.ShowStatus($"已删除 {removed} 个缓存账号。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus($"删除失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
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
            AppState.ShowStatus("没有可清空的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空缓存账号",
            Content = $"将清空全部 {_sourceItems.Count} 个缓存账号并删除头像缓存，此操作不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
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

            var cleared = AppState.LoginService.ClearCachedLoginAccounts();
            Reload();
            AppState.ShowStatus($"已清空 {cleared} 个缓存账号。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus($"清空失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private List<CachedSteamLoginAccount> GetSelectedAccounts()
    {
        return CachedAccountList.SelectedItems.OfType<CachedSteamLoginAccount>().ToList();
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

        var selectedCount = CachedAccountList.SelectedItems.Count;
        if (selectedCount > 1)
        {
            CachedDetailAvatar.ProfilePicture = null;
            CachedDetailAvatar.DisplayName = "多选";
            CachedDetailAccountNameText.Text = $"已选择 {selectedCount} 个账号";
            CachedDetailPersonaText.Text = "可批量删除";
            CachedDetailSteamIdText.Text = "可批量删除";
            CachedDetailTimeText.Text = "";
            return;
        }

        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            CachedDetailAvatar.ProfilePicture = null;
            CachedDetailAvatar.DisplayName = "未选择";
            CachedDetailAccountNameText.Text = "未选择账号";
            CachedDetailPersonaText.Text = "Steam 资料未同步";
            CachedDetailSteamIdText.Text = "Steam64 未记录";
            CachedDetailTimeText.Text = "暂无缓存时间";
            return;
        }

        CachedDetailAvatar.DisplayName = account.AccountTitle;
        CachedDetailAvatar.ProfilePicture = account.AvatarImage;
        CachedDetailAccountNameText.Text = account.AccountTitle;
        CachedDetailPersonaText.Text = account.PersonaDisplayName;
        CachedDetailSteamIdText.Text = account.SteamIdDisplay;
        CachedDetailTimeText.Text = $"缓存时间：{account.CachedAtText}";
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        var selectedCount = CachedAccountList.SelectedItems.Count;
        CachedAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        CachedSearchBox.IsEnabled = !isBusy;
        RefreshCachedButton.IsEnabled = !isBusy;
        DeleteCachedButton.IsEnabled = !isBusy && !_isDialogFlowActive && selectedCount > 0;
        ClearCachedButton.IsEnabled = !isBusy && !_isDialogFlowActive && _sourceItems.Count > 0;
        RestoreSelectedCachedButton.IsEnabled = !isBusy && selectedCount == 1;

        CachedSelectionHintText.Text = selectedCount > 1
            ? $"已选 {selectedCount} 项"
            : "Ctrl/Shift 可多选";
    }
}
