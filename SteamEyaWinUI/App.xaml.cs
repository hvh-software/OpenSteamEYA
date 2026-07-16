using Microsoft.UI.Xaml;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 在任何窗口/页面构造前确定界面语言，保证首帧即用所选语言渲染。
        Loc.Initialize(AppState.SettingsService);
        AppState.UpdateService.SetProxySite(AppState.SettingsService.Load().UpdateProxySite);

        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 崩溃前尽力落盘，便于排查 AOT 产物上的现场问题。
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamEYA");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响异常继续传播。
        }
    }
}
