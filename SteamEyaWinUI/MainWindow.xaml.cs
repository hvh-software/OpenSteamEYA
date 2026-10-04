using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Pages;
using SteamEyaWinUI.Services;
using WinRT;
using Windows.Graphics;

namespace SteamEyaWinUI;

public sealed partial class MainWindow : Window
{
    private static readonly string[] FontResourceKeys =
    [
        "XamlAutoFontFamily",
        "ContentControlThemeFontFamily",
        "TextControlThemeFontFamily"
    ];

    private static readonly (string Key, double Factor)[] GlassSurfaceResources =
    [
        ("CardBackgroundFillColorDefaultBrush", 1.0),
        ("CardBackgroundFillColorSecondaryBrush", 0.86),
        ("ControlFillColorDefaultBrush", 0.78),
        ("ControlFillColorSecondaryBrush", 0.66),
        ("ControlFillColorTertiaryBrush", 0.52),
        ("ControlFillColorInputActiveBrush", 0.92),
        ("LayerFillColorDefaultBrush", 0.72)
    ];

    private const int InitialWindowWidth = 1280;
    private const int InitialWindowHeight = 860;
    private const int MinWindowWidth = 1180;
    private const int MinWindowHeight = 780;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaColorNone = 0xFFFFFFFE;
    private const nuint WindowSubclassId = 1;

    private static nint s_hwnd;

    // Informational/Success 状态若干秒后自动收起；Warning/Error 常驻，直到被替换或用户手动关闭。
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(6);
    private readonly DispatcherQueueTimer _statusDismissTimer;
    private readonly FontFamily _defaultFontFamily;
    private readonly Dictionary<string, SolidColorBrush> _glassSurfaceBrushes = [];
    private FontFamily _currentFontFamily = null!;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _glassEffectEnabled;
    private int _backgroundOpacity = 72;
    private ElementTheme _requestedTheme = ElementTheme.Default;

    public static MainWindow? Instance { get; private set; }

    /// <summary>主窗口句柄，供文件/目录选择器等 WinRT 互操作（InitializeWithWindow）使用；在 ConfigureWindowSize 中赋值。</summary>
    public static nint Hwnd => s_hwnd;

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();
        Activated += OnWindowActivated;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        TitleVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";

        _defaultFontFamily = RootNavigationView.FontFamily;
        var settings = AppState.SettingsService.Load();
        ApplyTheme(ParseTheme(settings.Theme));
        ApplyGlassAppearance(settings.GlassEffectEnabled, settings.BackgroundOpacity);
        ApplyFontFamily(settings.FontFamily);
        RefreshNavText();
        Loc.LanguageChanged += RefreshNavText;

        _statusDismissTimer = DispatcherQueue.CreateTimer();
        _statusDismissTimer.Interval = StatusAutoDismissDelay;
        _statusDismissTimer.IsRepeating = false;
        _statusDismissTimer.Tick += (_, _) => StatusInfoBar.IsOpen = false;

        AppState.StatusReporter = ShowStatus;
        AppState.BusyChanged += OnBusyChanged;
        AppState.UpdateStateChanged += RefreshUpdateBadge;
        RefreshUpdateBadge();

        ContentFrame.Navigated += (_, _) =>
            DispatcherQueue.TryEnqueue(() => ApplyFontToVisualTree(Content, _currentFontFamily));
        RootNavigationView.ActualThemeChanged += (_, _) =>
        {
            UpdateTitleBarButtonColors();
            if (_requestedTheme == ElementTheme.Default)
            {
                QueueGlassSurfaceRefresh();
            }
        };

        ConfigureWindowSize();

        // 预载历史账号，登录页的头像/资料复用依赖该缓存。
        AppState.ReloadHistory();
        RootNavigationView.SelectedItem = LoginNavItem;

        // 首次启动即解析并持久化 Steam 安装路径（之后上号直接复用，不再每次探测）。
        // 等内容进入可视树（XamlRoot 就绪）后再跑，检测失败才需要弹框。
        RootNavigationView.Loaded += OnRootNavigationViewLoaded;

        _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
    }

    // 启动时解析 Steam 路径：成功则静默持久化；自动检测失败时弹框让用户手动选择含 steam.exe 的目录。
    private async void OnRootNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigationView.Loaded -= OnRootNavigationViewLoaded;
        ApplyFontToVisualTree(Content, _currentFontFamily);
        try
        {
            await SteamPathCoordinator.EnsureResolvedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("启动时解析 Steam 安装路径失败。", ex);
        }
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        // 状态可能来自后台线程（如登录后的 CS2 云推送进度），统一封送到 UI 线程。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatus(message, severity));
            return;
        }

        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;

        _statusDismissTimer.Stop();
        if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            _statusDismissTimer.Start();
        }
    }

    /// <summary>有新版本时在「关于」导航项上亮红点，代替曾经常驻底部的更新横幅。</summary>
    private void RefreshUpdateBadge()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshUpdateBadge);
            return;
        }

        AboutNavItem.InfoBadge = AppState.LatestUpdate?.IsUpdateAvailable == true
            ? new InfoBadge()
            : null;
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
            "loadout" => typeof(LoadoutPage),
            "personalization" => typeof(PersonalizationPage),
            "settings" => typeof(SettingsPage),
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

    /// <summary>把主题套用到内容根（无打包下 Application.RequestedTheme 不可后置，故走根元素 RequestedTheme）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        _requestedTheme = theme;
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
            UpdateTitleBarButtonColors(theme);

            if (_glassEffectEnabled && theme is ElementTheme.Light or ElementTheme.Dark)
            {
                ApplyGlassSurfaceResources(_backgroundOpacity, theme);
            }

            QueueGlassSurfaceRefresh(theme is ElementTheme.Light or ElementTheme.Dark ? theme : null);
        }
    }

    /// <summary>切换 Desktop Acrylic，并用透明表面资源让背景透过控件而不降低文字透明度。</summary>
    public void ApplyGlassAppearance(bool enabled, int backgroundOpacity)
    {
        _glassEffectEnabled = enabled;
        _backgroundOpacity = Math.Clamp(
            backgroundOpacity,
            AppSettings.MinimumBackgroundOpacity,
            AppSettings.MaximumBackgroundOpacity);

        if (enabled)
        {
            EnsureAcrylicController();
            ApplyAcrylicOpacity(_backgroundOpacity);
            ApplyGlassSurfaceResources(_backgroundOpacity);
        }
        else
        {
            DisposeAcrylicController();
            SystemBackdrop = new MicaBackdrop();
            foreach (var (key, _) in GlassSurfaceResources)
            {
                Application.Current.Resources.Remove(key);
            }

            _glassSurfaceBrushes.Clear();
        }

        UpdateTitleBarButtonColors();
    }

    public void ApplyGlassSurfaceOpacity(int opacity)
    {
        _backgroundOpacity = Math.Clamp(
            opacity,
            AppSettings.MinimumBackgroundOpacity,
            AppSettings.MaximumBackgroundOpacity);
        if (_glassEffectEnabled)
        {
            ApplyAcrylicOpacity(_backgroundOpacity);
            ApplyGlassSurfaceResources(_backgroundOpacity);
        }
    }

    private void EnsureAcrylicController()
    {
        if (_acrylicController is not null)
        {
            return;
        }

        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        SystemBackdrop = null;
        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = GetBackdropTheme()
        };
        _acrylicController = new DesktopAcrylicController();
        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private void ApplyAcrylicOpacity(int opacityPercent)
    {
        if (_acrylicController is null)
        {
            return;
        }

        var opacity = Math.Clamp(opacityPercent / 100f, 0, 1);
        var theme = _requestedTheme == ElementTheme.Default ? RootNavigationView.ActualTheme : _requestedTheme;
        _acrylicController.TintColor = theme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 32, 32, 32);
        _acrylicController.TintOpacity = opacity;
        _acrylicController.LuminosityOpacity = opacity;
    }

    private SystemBackdropTheme GetBackdropTheme() =>
        (_requestedTheme == ElementTheme.Default ? RootNavigationView.ActualTheme : _requestedTheme) switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default
        };

    private void UpdateTitleBarButtonColors(ElementTheme? theme = null)
    {
        var effectiveTheme = theme is ElementTheme.Light or ElementTheme.Dark
            ? theme.Value
            : _requestedTheme == ElementTheme.Default
                ? RootNavigationView.ActualTheme
                : _requestedTheme;
        var foreground = effectiveTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = transparent;
    }

    private void DisposeAcrylicController()
    {
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }
    }

    private void ApplyGlassSurfaceResources(int opacityPercent, ElementTheme? theme = null)
    {
        var effectiveTheme = theme ?? (_requestedTheme == ElementTheme.Default
            ? RootNavigationView.ActualTheme
            : _requestedTheme);
        var baseColor = effectiveTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 32, 32, 32);

        foreach (var (key, factor) in GlassSurfaceResources)
        {
            var alpha = (byte)Math.Clamp(Math.Round(255 * opacityPercent / 100.0 * factor), 0, 255);
            var color = Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
            if (_glassSurfaceBrushes.TryGetValue(key, out var brush))
            {
                brush.Color = color;
            }
            else
            {
                brush = new SolidColorBrush(color);
                _glassSurfaceBrushes[key] = brush;
                Application.Current.Resources[key] = brush;
            }
        }
    }

    private void QueueGlassSurfaceRefresh(ElementTheme? theme = null)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_glassEffectEnabled)
            {
                if (_backdropConfiguration is not null)
                {
                    _backdropConfiguration.Theme = GetBackdropTheme();
                }
                ApplyAcrylicOpacity(_backgroundOpacity);
                ApplyGlassSurfaceResources(_backgroundOpacity, theme);
            }
        });
    }

    /// <summary>覆盖 WinUI 控件模板字体资源，并即时刷新当前可视树；图标字体不参与替换。</summary>
    public void ApplyFontFamily(string? familyName)
    {
        var family = string.IsNullOrWhiteSpace(familyName)
            ? _defaultFontFamily
            : new FontFamily(familyName);

        _currentFontFamily = family;
        foreach (var key in FontResourceKeys)
        {
            if (string.IsNullOrWhiteSpace(familyName))
            {
                Application.Current.Resources.Remove(key);
            }
            else
            {
                Application.Current.Resources[key] = family;
            }
        }

        RootNavigationView.FontFamily = family;
        ApplyFontToVisualTree(Content, family);
    }

    private static void ApplyFontToVisualTree(DependencyObject? root, FontFamily family)
    {
        if (root is null || root is IconElement)
        {
            return;
        }

        switch (root)
        {
            case Control control:
                control.FontFamily = family;
                break;
            case TextBlock textBlock:
                textBlock.FontFamily = family;
                break;
            case RichTextBlock richTextBlock:
                richTextBlock.FontFamily = family;
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyFontToVisualTree(VisualTreeHelper.GetChild(root, index), family);
        }
    }

    private static ElementTheme ParseTheme(string theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>本地化导航项文字；语言切换时由 Loc.LanguageChanged 再次调用。</summary>
    private void RefreshNavText()
    {
        LoginNavItem.Content = Loc.T("Nav_Login");
        HistoryNavItem.Content = Loc.T("Nav_History");
        CachedAccountsNavItem.Content = Loc.T("Nav_CachedAccounts");
        LoadoutNavItem.Content = Loc.T("Nav_Loadout");
        PersonalizationNavItem.Content = Loc.T("Nav_Personalization");
        SettingsNavItem.Content = Loc.T("Nav_Settings");
        AboutNavItem.Content = Loc.T("Nav_About");
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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var borderColor = DwmwaColorNone;
            _ = DwmSetWindowAttribute(
                s_hwnd,
                DwmwaBorderColor,
                ref borderColor,
                (uint)sizeof(uint));
        }

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
        Activated -= OnWindowActivated;
        DisposeAcrylicController();
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

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        nint hwnd,
        uint attribute,
        ref uint attributeValue,
        uint attributeSize);

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
