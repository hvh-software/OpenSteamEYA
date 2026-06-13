using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SteamEyaWinUI.Pages;

public sealed partial class HistoryPage : Page
{
    private readonly ObservableCollection<SteamAccountHistoryItem> _viewItems = [];
    private readonly ObservableCollection<AccountImportEntry> _importEntries = [];

    /// <summary>
    /// 当前从磁盘加载的完整列表快照（即 AppState.HistoryAccounts）。搜索/过滤只在此内存列表上做，
    /// 每个键击不再回读磁盘；仅 HistoryChanged 与进入页面时才刷新此快照。
    /// </summary>
    private IReadOnlyList<SteamAccountHistoryItem> _allItems = [];

    /// <summary>搜索框去抖计时器：输入停止约 300ms 后才执行一次过滤，避免逐键击全量重建。</summary>
    private readonly DispatcherQueueTimer _searchDebounceTimer;

    /// <summary>页面是否处于活动（已导航到、未离开）状态，用于不可见时延迟重建。</summary>
    private bool _isActive;

    /// <summary>
    /// 不可见期间收到 HistoryChanged 时只更新快照并记下待选中 SteamID（null 表示保持当前选择），
    /// 不做整列表重建；下次 OnNavigatedTo 经 ReloadHistory 统一重建一次。
    /// </summary>
    private string? _pendingSelectSteamId;

    /// <summary>
    /// 对话框流程重入门闩：同一 XamlRoot 同时只能打开一个 ContentDialog，二次 ShowAsync 直接抛异常。
    /// 导入流程在读剪贴板的 await 与 ShowAsync 之间存在挂起窗口，期间再点导入/删除/清空都必须拦下。
    /// </summary>
    private bool _isDialogFlowActive;

    public HistoryPage()
    {
        InitializeComponent();
        HistoryAccountList.ItemsSource = _viewItems;
        ImportDialogList.ItemsSource = _importEntries;

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += (_, _) => RebuildView(GetSelectedSteamId());

        AppState.HistoryChanged += OnHistoryChanged;
        AppState.BusyChanged += _ => UpdateControlsEnabled();

        // 取用页面创建前积累的选中意图（首次构造时 _viewItems 为空，GetSelectedSteamId 必为 null）。
        var pending = AppState.PendingHistorySelection;
        AppState.PendingHistorySelection = null;
        _allItems = AppState.HistoryAccounts;
        RebuildView(pending);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;

        // 不可见期间累积的待选中意图优先于当前选择；ReloadHistory 会刷新快照并触发重建。
        var select = _pendingSelectSteamId ?? GetSelectedSteamId();
        _pendingSelectSteamId = null;
        AppState.ReloadHistory(select);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
        // 停掉去抖计时器，避免离开页面后 300ms 窗口内 Tick 仍对离屏页面触发一次无谓重建。
        _searchDebounceTimer.Stop();
    }

    private void CancelHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ShowStatus("正在取消...", InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    private void OnHistoryChanged(string? selectSteamId)
    {
        // 进入页面时 OnNavigatedTo 触发的 ReloadHistory 会先于此处把 _isActive 置 true，正常重建；
        // 页面不可见时（其它页触发的后台刷新）只更新内存快照并记下待选中意图，下次 OnNavigatedTo 再重建。
        _allItems = AppState.HistoryAccounts;
        if (!_isActive)
        {
            _pendingSelectSteamId = selectSteamId ?? _pendingSelectSteamId;
            return;
        }

        RebuildView(selectSteamId ?? GetSelectedSteamId());
    }

    private void RebuildView(string? selectSteamId)
    {
        // 过滤只在内存快照上做，不回读磁盘。
        var source = _allItems;
        var filter = HistorySearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(account => Matches(account, filter)).ToList();

        // 重建会清掉 ListView 的选中态，且列表元素每次都是新实例；
        // 先按账号键记住当前（可能是多项）选择，重建后恢复——后台资料同步等
        // 延迟触发的刷新不应把用户进行中的多选打回单选。
        var selectedKeys = HistoryAccountList.SelectedItems
            .OfType<SteamAccountHistoryItem>()
            .Select(AccountHistoryService.GetAccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectSteamId))
        {
            // 仅有 SteamID 字符串、无完整账号对象，故直接构造与 GetAccountKey 一致的 "id:" 键。
            selectedKeys.Add($"id:{selectSteamId}");
        }

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var toSelect = _viewItems
            .Where(account => selectedKeys.Contains(AccountHistoryService.GetAccountKey(account)))
            .ToList();
        if (toSelect.Count <= 1)
        {
            HistoryAccountList.SelectedItem = toSelect.Count == 1 ? toSelect[0] : _viewItems.FirstOrDefault();
        }
        else
        {
            foreach (var account in toSelect)
            {
                HistoryAccountList.SelectedItems.Add(account);
            }
        }

        var hasAny = source.Count > 0;
        HistoryEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Text = hasAny ? "没有匹配的账号" : "暂无历史账号";
        HistoryEmptyHintText.Text = hasAny
            ? "换个关键词试试，或清空搜索框。"
            : "成功登录或一键查询后会自动记录账号。";
        HistorySummaryText.Text = hasAny
            ? $"共 {source.Count} 个账号，登录或查询过的账号会自动记录在这里。"
            : "登录或查询过的账号会自动记录在这里。";

        UpdateDetail();
        UpdateControlsEnabled();
    }

    private static bool Matches(SteamAccountHistoryItem account, string filter)
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

    private void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // 去抖：连续输入只 Start/重置计时器，停止输入约 300ms 后才真正过滤重建。
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ReloadHistory(GetSelectedSteamId());
        AppState.ShowStatus("历史账号已刷新。", InfoBarSeverity.Success);
    }

    private void HistoryAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private void ExportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = GetSelectedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus("请先选择要导出的账号（Ctrl/Shift 可多选）。", InfoBarSeverity.Error);
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            accounts.Select(account => $"{account.AccountName}----{account.EyaToken}"));

        var package = new DataPackage();
        package.SetText(text);

        try
        {
            // 令牌为敏感凭据：不进 Win+V 剪贴板历史、不随云剪贴板漫游。
            var options = new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };
            if (!Clipboard.SetContentWithOptions(package, options))
            {
                // 个别系统配置下 SetContentWithOptions 可能返回 false；回退到普通写入保证导出可用。
                Clipboard.SetContent(package);
            }
        }
        catch (COMException)
        {
            AppState.ShowStatus("写入剪贴板失败，请重试。", InfoBarSeverity.Error);
            return;
        }

        try
        {
            // 不 Flush 的话内容由本进程延迟渲染，应用退出后剪贴板就空了；Flush 失败不影响本次粘贴。
            Clipboard.Flush();
        }
        catch (COMException)
        {
        }

        AppState.ShowStatus(
            $"已导出 {accounts.Count} 个账号到剪贴板（账户名----令牌，每行一个）。",
            InfoBarSeverity.Success);
    }

    private async void ImportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        List<AccountImportEntry>? selected;
        _isDialogFlowActive = true;
        try
        {
            selected = await PickImportEntriesAsync();
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        if (selected is null || selected.Count == 0)
        {
            return;
        }

        AppState.SetBusy(true);
        try
        {
            var (added, updated) = AppState.AccountHistoryService.ImportAccounts(selected);
            AppState.ReloadHistory(selected[0].SteamId);
            AppState.ShowStatus(
                $"导入完成：新增 {added} 个，更新 {updated} 个，正在后台同步 Steam 资料...",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus($"导入失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }
        finally
        {
            AppState.SetBusy(false);
        }

        // 后台补全昵称/头像，完成后刷新列表（不占用全局忙碌状态，失败不影响导入结果）。
        try
        {
            var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync(
                selected.Select(entry => entry.SteamId).ToList());
            if (refreshed > 0)
            {
                AppState.ReloadHistory(GetSelectedSteamId());
                AppState.ShowStatus($"Steam 资料同步完成（{refreshed} 个账号）。", InfoBarSeverity.Success);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>读剪贴板 → 解析 → 弹勾选对话框；返回用户确认导入的条目，取消或无可导入内容时返回 null。</summary>
    private async Task<List<AccountImportEntry>?> PickImportEntriesAsync()
    {
        string clipboardText;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                AppState.ShowStatus("剪贴板中没有文本内容。", InfoBarSeverity.Error);
                return null;
            }

            clipboardText = await content.GetTextAsync();
        }
        catch (COMException)
        {
            AppState.ShowStatus("读取剪贴板失败，请重试。", InfoBarSeverity.Error);
            return null;
        }

        var (entries, invalidCount) = ParseImportText(clipboardText);
        if (entries.Count == 0)
        {
            AppState.ShowStatus(
                invalidCount > 0
                    ? $"剪贴板中的 {invalidCount} 行都无法识别，需要“账户名----令牌”格式（每行一个）。"
                    : "剪贴板中没有可导入的账号，需要“账户名----令牌”格式（每行一个）。",
                InfoBarSeverity.Error);
            return null;
        }

        _importEntries.Clear();
        foreach (var entry in entries)
        {
            _importEntries.Add(entry);
        }

        ImportDialogSummaryText.Text = invalidCount > 0
            ? $"识别到 {entries.Count} 个账号（另有 {invalidCount} 行无法识别），勾选要导入的账号："
            : $"识别到 {entries.Count} 个账号，勾选要导入的账号：";
        ImportDialogList.SelectAll();

        if (await ImportDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return ImportDialogList.SelectedItems.OfType<AccountImportEntry>().ToList();
    }

    private void ImportDialogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ImportDialog.IsPrimaryButtonEnabled = ImportDialogList.SelectedItems.Count > 0;
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var accounts = GetSelectedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus("请先选择要删除的账号（Ctrl/Shift 可多选）。", InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join("、", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? $"将删除 {nameText} 等 {accounts.Count} 个账号，仅移除本地记录与头像缓存。"
            : $"将删除 {nameText}（共 {accounts.Count} 个），仅移除本地记录与头像缓存。";

        var dialog = new ContentDialog
        {
            Title = "删除历史账号",
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

            var removed = AppState.AccountHistoryService.DeleteAccounts(accounts);
            AppState.ReloadHistory();
            AppState.ShowStatus($"已删除 {removed} 个历史账号。", InfoBarSeverity.Success);
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

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var total = AppState.HistoryAccounts.Count;
        if (total == 0)
        {
            AppState.ShowStatus("没有可清空的历史账号。", InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空历史账号",
            Content = $"将清空全部 {total} 个历史账号并删除头像缓存，此操作不可恢复。",
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

            var cleared = AppState.AccountHistoryService.ClearAll();
            AppState.ReloadHistory();
            AppState.ShowStatus($"已清空 {cleared} 个历史账号。", InfoBarSeverity.Success);
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

    private static (List<AccountImportEntry> Entries, int InvalidCount) ParseImportText(string text)
    {
        var entries = new List<AccountImportEntry>();
        var seenSteamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidCount = 0;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseCredentials(line, out var accountName, out var token, out var info))
            {
                invalidCount++;
                continue;
            }

            var steamId = info.SteamId!;

            // 同一账号出现多行时取第一行。
            if (!seenSteamIds.Add(steamId))
            {
                continue;
            }

            entries.Add(new AccountImportEntry
            {
                AccountName = accountName,
                EyaToken = token,
                SteamId = steamId,
                TokenExpiresAt = info.ExpiresAt,
                TokenIsValid = info.IsValid,
                TokenStatus = info.Status,
                AlreadyExists = AppState.HistoryAccounts.Any(account =>
                    string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
            });
        }

        return (entries, invalidCount);
    }

    /// <summary>
    /// 解析一行凭据。逐个尝试候选切分（“----”各列、空白各列），以“令牌能解析出 SteamID”为准。
    /// 单列候选排在拼接候选之前，确保“账号----密码----令牌”这类多列行取到干净的令牌
    /// 而不是把“密码----令牌”整串当令牌存入。
    /// </summary>
    private static bool TryParseCredentials(
        string line,
        out string accountName,
        out string token,
        out JwtTokenInfo info)
    {
        foreach (var (name, candidateToken) in EnumerateCredentialCandidates(line))
        {
            if (name.Length == 0 || candidateToken.Length == 0)
            {
                continue;
            }

            var normalized = FormatHelper.NormalizeToken(candidateToken);
            JwtTokenInfo candidateInfo;
            try
            {
                candidateInfo = AppState.JwtTokenService.Inspect(normalized);
            }
            catch (Exception)
            {
                // 剪贴板内容不可信，畸形伪 JWT 一律按无法识别处理，绝不允许解析把应用带崩。
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidateInfo.SteamId))
            {
                accountName = name;
                token = normalized;
                info = candidateInfo;
                return true;
            }
        }

        accountName = "";
        token = "";
        info = new JwtTokenInfo(null, null, false, "无法识别", null);
        return false;
    }

    private static IEnumerable<(string AccountName, string Token)> EnumerateCredentialCandidates(string line)
    {
        // “账户名----令牌”或“账户名----密码----令牌”等多列格式：
        // 账户名取第一列，令牌依次尝试其余各列。
        var columns = line.Split("----", StringSplitOptions.None);
        if (columns.Length >= 2)
        {
            var name = columns[0].Trim();
            for (var i = 1; i < columns.Length; i++)
            {
                yield return (name, columns[i].Trim());
            }

            // 极端情况：令牌本身含“----”（base64url 字符集含 '-'），把第一列之后的内容整体再试一次。
            if (columns.Length > 2)
            {
                yield return (name, string.Join("----", columns[1..]).Trim());
            }
        }

        // 兼容空格 / Tab 分隔的“账户名 令牌 [备注…]”格式：令牌依次尝试各列。
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length >= 2)
        {
            for (var i = 1; i < fields.Length; i++)
            {
                yield return (fields[0].Trim(), fields[i].Trim());
            }
        }
    }

    private List<SteamAccountHistoryItem> GetSelectedAccounts()
    {
        return HistoryAccountList.SelectedItems.OfType<SteamAccountHistoryItem>().ToList();
    }

    private async void OneClickHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus("请选择历史账号。", InfoBarSeverity.Error);
            return;
        }

        if (AppState.LoginPage is not { } loginPage)
        {
            AppState.ShowStatus("登录页尚未初始化，请先打开登录页。", InfoBarSeverity.Error);
            return;
        }

        // 与登录页统一走可取消的忙碌机制：一键查询最长可达上百秒，必须能被取消按钮中断。
        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus($"正在一键查询 {account.AccountTitle} 的账号状态...", InfoBarSeverity.Informational);

        try
        {
            var score = await loginPage.QueryAndSaveCsStatusAsync(
                account.AccountName, account.EyaToken, cancellationToken);
            AppState.ShowStatus(
                $"{account.AccountTitle} 查询完成：优先分 {score.DisplayText}，CS2等级 {score.PlayerLevelText}，冷却 {score.CooldownText}，GC VAC {score.GcVacText}。",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus("已取消一键查询。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void UseHistoryAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus("请选择历史账号。", InfoBarSeverity.Error);
            return;
        }

        MainWindow.Instance?.LoadAccountIntoLogin(account);
    }

    private string? GetSelectedSteamId()
    {
        return HistoryAccountList.SelectedItem is SteamAccountHistoryItem account
            ? account.SteamId
            : null;
    }

    private void UpdateDetail()
    {
        if (HistoryDetailAccountNameText is null)
        {
            return;
        }

        var selectedCount = HistoryAccountList.SelectedItems.Count;
        if (selectedCount > 1)
        {
            HistoryDetailAvatar.ProfilePicture = null;
            HistoryDetailAvatar.DisplayName = "多选";
            HistoryDetailAccountNameText.Text = $"已选择 {selectedCount} 个账号";
            HistoryDetailPersonaText.Text = "可批量导出或删除";
            HistoryDetailSteamIdText.Text = "—";
            HistoryDetailTokenExpiresText.Text = "—";
            HistoryDetailLastLoginText.Text = "—";
            HistoryDetailCompetitiveScoreText.Text = "—";
            HistoryDetailCsLevelText.Text = "—";
            HistoryDetailCooldownStatusText.Text = "—";
            HistoryDetailAccountStatusText.Text = "—";
            return;
        }

        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            HistoryDetailAvatar.ProfilePicture = null;
            HistoryDetailAvatar.DisplayName = "未选择";
            HistoryDetailAccountNameText.Text = "未选择账号";
            HistoryDetailPersonaText.Text = "Steam 资料未同步";
            HistoryDetailSteamIdText.Text = "未解析";
            HistoryDetailTokenExpiresText.Text = "未解析";
            HistoryDetailLastLoginText.Text = "暂无记录";
            HistoryDetailCompetitiveScoreText.Text = "待查询";
            HistoryDetailCsLevelText.Text = "待查询";
            HistoryDetailCooldownStatusText.Text = "待查询";
            HistoryDetailAccountStatusText.Text = "待查询";
            return;
        }

        HistoryDetailAvatar.DisplayName = account.AccountTitle;
        HistoryDetailAvatar.ProfilePicture = account.AvatarImage;
        HistoryDetailAccountNameText.Text = account.AccountTitle;
        HistoryDetailPersonaText.Text = account.PersonaDisplayName;
        HistoryDetailSteamIdText.Text = account.SteamIdDisplay;
        HistoryDetailTokenExpiresText.Text = account.TokenExpiresText;
        HistoryDetailLastLoginText.Text = account.LastLoginText;
        HistoryDetailCompetitiveScoreText.Text = account.CompetitiveScoreText;
        HistoryDetailCsLevelText.Text = account.CsPlayerLevelText;
        HistoryDetailCooldownStatusText.Text = account.CooldownStatusText;
        HistoryDetailAccountStatusText.Text = account.JwtAvailabilityText;
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        var selectedCount = HistoryAccountList.SelectedItems.Count;
        HistoryAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        RefreshHistoryButton.IsEnabled = !isBusy;
        HistorySearchBox.IsEnabled = !isBusy;
        ImportHistoryButton.IsEnabled = !isBusy;
        ExportHistoryButton.IsEnabled = !isBusy && selectedCount > 0;
        DeleteHistoryButton.IsEnabled = !isBusy && selectedCount > 0;
        ClearHistoryButton.IsEnabled = !isBusy && AppState.HistoryAccounts.Count > 0;
        OneClickHistoryQueryButton.IsEnabled = !isBusy && selectedCount == 1;
        UseHistoryAccountButton.IsEnabled = !isBusy && selectedCount == 1;

        // 取消按钮仅忙碌时出现且保持可用，让用户中断本页发起的一键查询。
        CancelHistoryQueryButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;

        SelectionHintText.Text = selectedCount > 1
            ? $"已选 {selectedCount} 项"
            : "Ctrl/Shift 可多选";
    }
}
