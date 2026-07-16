using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class AboutPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private bool _isDownloadingUpdate;
    private long _downloadBytesReceived;
    private long? _downloadTotalBytes;

    public AboutPage()
    {
        InitializeComponent();
        AppState.UpdateStateChanged += Render;
        Loc.LanguageChanged += OnLanguageChanged;
        Render();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (AppState.LatestUpdate is null && !AppState.IsCheckingForUpdates)
        {
            _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
        }
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；命令式文本（版本/更新状态/日志）重跑 Render 即可换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            Render();
        });
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.CheckForUpdatesAsync(isAutomatic: false);
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var update = AppState.LatestUpdate;
        var url = update?.ArtifactUrl ?? update?.ReleaseUrl;
        if (update is null || string.IsNullOrWhiteSpace(url))
        {
            AppState.ShowStatus(Loc.T("About_NoDownloadInfo"), InfoBarSeverity.Warning);
            return;
        }

        if (!update.IsUpdateAvailable)
        {
            AppState.ShowStatus(Loc.Tf("About_Update_UpToDate_Format", update.LatestTag), InfoBarSeverity.Success);
            return;
        }

        if (_isDownloadingUpdate)
        {
            return;
        }

        // 若最新产物不是安装器，退化为打开发布页。
        if (!url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(update.ArtifactType, "exe-installer", StringComparison.OrdinalIgnoreCase))
        {
            await AppState.OpenUrlAsync(url);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("About_UpdateInstallConfirm_Title"),
            Content = Loc.Tf("About_UpdateInstallConfirm_Content_Format", update.LatestTag),
            PrimaryButtonText = Loc.T("About_UpdateInstallConfirm_Install"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _isDownloadingUpdate = true;
        _downloadBytesReceived = 0;
        _downloadTotalBytes = null;
        Render();

        try
        {
            AppState.ShowStatus(Loc.T("About_Update_Downloading"), InfoBarSeverity.Informational);

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                _downloadBytesReceived = p.BytesReceived;
                _downloadTotalBytes = p.TotalBytes;
                Render();
            });

            var installerPath = await AppState.UpdateInstallerService.DownloadInstallerAsync(update, progress);
            AppState.ShowStatus(Loc.T("About_Update_Downloaded"), InfoBarSeverity.Success);

            if (!AppState.UpdateInstallerService.LaunchInstaller(installerPath))
            {
                AppState.ShowStatus(Loc.T("About_Update_InstallerLaunchFailed"), InfoBarSeverity.Error);
                return;
            }

            AppState.ShowStatus(Loc.T("About_Update_InstallerLaunched"), InfoBarSeverity.Warning);

            // 安装器已启动：先清掉残留实例，再强制结束当前进程，避免文件占用导致安装失败。
            var current = Process.GetCurrentProcess();
            AppState.UpdateInstallerService.ForceCloseOtherInstances(current.ProcessName, current.Id);
            current.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("About_Update_DownloadFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDownloadingUpdate = false;
            _downloadBytesReceived = 0;
            _downloadTotalBytes = null;
            Render();
        }
    }

    private async void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(AppState.LatestUpdate?.ReleaseUrl ?? GitHubUpdateService.ReleasesUrl);
    }

    private async void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(GitHubUpdateService.RepositoryUrl);
    }

    private void Render()
    {
        var update = AppState.LatestUpdate;
        var isChecking = AppState.IsCheckingForUpdates;

        UpdateCheckingRing.IsActive = isChecking;
        UpdateCheckingRing.Visibility = isChecking ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = !isChecking && !_isDownloadingUpdate;
        DownloadUpdateButton.IsEnabled = !isChecking &&
            !_isDownloadingUpdate &&
            update is { IsUpdateAvailable: true } &&
            !string.IsNullOrWhiteSpace(update.ArtifactUrl);

        UpdateDownloadProgressPanel.Visibility = _isDownloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (_isDownloadingUpdate)
        {
            if (_downloadTotalBytes is > 0)
            {
                var percent = Math.Clamp(_downloadBytesReceived * 100d / _downloadTotalBytes.Value, 0d, 100d);
                UpdateDownloadProgressBar.IsIndeterminate = false;
                UpdateDownloadProgressBar.Value = percent;
                UpdateDownloadProgressText.Text = Loc.Tf(
                    "About_Update_DownloadProgress_Format",
                    percent.ToString("F1"),
                    FormatHelper.FormatFileSize(_downloadBytesReceived),
                    FormatHelper.FormatFileSize(_downloadTotalBytes.Value));
            }
            else
            {
                UpdateDownloadProgressBar.IsIndeterminate = true;
                UpdateDownloadProgressText.Text = Loc.T("About_Update_DownloadProgress_Unknown");
            }
        }

        AboutVersionText.Text = Loc.Tf("About_Version_Format", update?.CurrentVersion ?? GitHubUpdateService.CurrentVersion);

        if (isChecking)
        {
            AboutUpdateStatusText.Text = Loc.T("About_Update_Connecting");
            AboutUpdateCheckedText.Text = Loc.T("About_CheckedAt_Checking");
            return;
        }

        if (AppState.UpdateCheckError is { } error)
        {
            AboutUpdateStatusText.Text = Loc.Tf("About_Update_ConnectFail_Format", error);
            AboutArtifactText.Text = Loc.T("About_Artifact_ReadFail");
            AboutUpdateCheckedText.Text = AppState.UpdateCheckedAt.HasValue
                ? Loc.Tf("About_CheckedAt_Format", FormatHelper.FormatDateTime(AppState.UpdateCheckedAt.Value))
                : Loc.T("About_CheckedAt_Never");
            AboutChangelogText.Text = Loc.T("About_Changelog_ReadFail");
            return;
        }

        if (update is null)
        {
            AboutUpdateStatusText.Text = Loc.T("About_Update_AutoHint");
            AboutArtifactText.Text = Loc.T("About_Artifact_Never");
            AboutUpdateCheckedText.Text = Loc.T("About_CheckedAt_Never");
            AboutChangelogText.Text = Loc.T("About_Changelog_Never");
            return;
        }

        AboutUpdateStatusText.Text = update.IsUpdateAvailable
            ? Loc.Tf("About_Update_Available_Format", update.LatestTag)
            : Loc.Tf("About_Update_UpToDate_Format", update.LatestTag);
        AboutArtifactText.Text = string.IsNullOrWhiteSpace(update.ArtifactName)
            ? Loc.T("About_Artifact_NoAsset")
            : Loc.Tf("About_Artifact_Format", update.ArtifactName, FormatHelper.FormatFileSize(update.ArtifactSize));
        AboutUpdateCheckedText.Text = Loc.Tf("About_CheckedAt_Format", FormatHelper.FormatDateTime(update.CheckedAt));
        AboutChangelogText.Text = update.Changelog.Count == 0
            ? Loc.T("About_Changelog_Empty")
            : Loc.T("About_Changelog_Header") + "\n" + string.Join('\n', update.Changelog.Take(8));
    }
}
