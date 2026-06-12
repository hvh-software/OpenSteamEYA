using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Pages;
using SteamEyaWinUI.Services;
using Windows.Graphics;

namespace SteamEyaWinUI;

public sealed partial class MainWindow : Window
{
    private const int InitialWindowWidth = 1280;
    private const int InitialWindowHeight = 860;
    private const int MinWindowWidth = 1180;
    private const int MinWindowHeight = 780;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint WindowSubclassId = 1;

    private static nint s_hwnd;

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        TitleVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";

        AppState.StatusReporter = ShowStatus;
        AppState.BusyChanged += OnBusyChanged;

        ConfigureWindowSize();

        // 预载历史账号，登录页的头像/资料复用依赖该缓存。
        AppState.ReloadHistory();
        RootNavigationView.SelectedItem = LoginNavItem;

        _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
    }

    /// <summary>历史页“载入到登录页”：切到登录页并填充账号。</summary>
    public void LoadAccountIntoLogin(SteamAccountHistoryItem account)
    {
        RootNavigationView.SelectedItem = LoginNavItem;
        AppState.LoginPage?.LoadHistoryAccount(account);
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string pageName)
        {
            NavigateTo(pageName);
        }
    }

    private void NavigateTo(string pageName)
    {
        var pageType = pageName switch
        {
            "history" => typeof(HistoryPage),
            "cachedAccounts" => typeof(CachedAccountsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LoginPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
        }
    }

    private void OnBusyChanged(bool isBusy)
    {
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private unsafe void ConfigureWindowSize()
    {
        s_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(s_hwnd) / 96.0;

        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(InitialWindowWidth * scale),
            (int)Math.Ceiling(InitialWindowHeight * scale)));

        // 用 WM_GETMINMAXINFO 子类化实时按当前 DPI 计算最小尺寸，
        // 跨多显示器 / DPI 变化时 OverlappedPresenter.PreferredMinimum*（启动时固定的物理像素）会失效。
        SetWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId, 0);
        Closed += OnClosed;
    }

    private unsafe void OnClosed(object sender, WindowEventArgs args)
    {
        RemoveWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint SubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var info = (MinMaxInfo*)lParam;
            info->MinTrackSize.X = (int)Math.Ceiling(MinWindowWidth * scale);
            info->MinTrackSize.Y = (int)Math.Ceiling(MinWindowHeight * scale);
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
