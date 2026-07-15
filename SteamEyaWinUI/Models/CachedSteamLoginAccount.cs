using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Models;

public sealed partial class CachedSteamLoginAccount : INotifyPropertyChanged
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string? PersonaName { get; set; }

    private string? _avatarUrl;

    public string? AvatarUrl
    {
        get => _avatarUrl;
        set
        {
            if (_avatarUrl == value)
            {
                return;
            }

            _avatarUrl = value;
            InvalidateAvatar();
        }
    }

    private string? _avatarPath;

    public string? AvatarPath
    {
        get => _avatarPath;
        set
        {
            if (_avatarPath == value)
            {
                return;
            }

            _avatarPath = value;
            InvalidateAvatar();
        }
    }

    public DateTimeOffset CachedAt { get; set; }

    // local.vdf 的 ConnectCache 令牌（crc32(账户名)+"1" → DPAPI 加密的刷新令牌 hex）。
    // EYA 登录会整体覆盖 local.vdf，故在覆盖前抓取并随账号持久化，恢复时原样写回，Steam 才能免密自动登录。
    // 该 blob 已由 Steam 用 DPAPI（当前用户）加密，仅本机本用户可解，敏感度与 local.vdf 本身相当。
    public string? ConnectCacheToken { get; set; }

    [JsonIgnore]
    public string CacheKey => string.IsNullOrWhiteSpace(SteamId) ? $"name:{AccountName}" : $"id:{SteamId}";

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? Loc.T("Cached_Title_Unknown") : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? Loc.T("Cached_Persona_NotSynced") : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? Loc.T("Cached_Steam64_NotRecorded") : SteamId;

    [JsonIgnore]
    public string CachedAtText => CachedAt == default
        ? Loc.T("Cached_CachedAt_UnknownTime")
        : CachedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string CachedAtShortText => CachedAt == default
        ? Loc.T("Cached_CachedAt_Unknown")
        : CachedAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string CachedAtCaptionText => Loc.Tf("Cached_Card_CachedAt_Caption_Format", CachedAtShortText);

    // 进程级头像缓存：列表重建会换新实例，仅靠实例字段无法跨重建复用，必须用静态字典才真正止血。
    // 键 = 完整路径，值带最后写入时间：mtime 不符就重新解码并「替换」同键条目——每次刷新都会重写头像文件
    //（mtime 必变），若把 mtime 编进键，旧解码位图会永久滞留字典（每轮刷新 × 每账号泄漏一份）。
    // 仅 UI 线程访问（BitmapImage 也只能在 UI 线程使用），普通 Dictionary 即可。
    private static readonly Dictionary<string, (DateTime Mtime, BitmapImage Image)> AvatarCache =
        new(StringComparer.OrdinalIgnoreCase);

    // PersonPicture 最大显示 92px（账号详情），按 2 倍留 DPI 余量解码。
    private const int AvatarDecodePixelWidth = 184;

    private BitmapImage? _avatarImage;

    [JsonIgnore]
    public BitmapImage? AvatarImage => _avatarImage ??= LoadAvatarImage();

    // 头像来源（路径/URL）变化时丢弃已解码缓存并通知绑定重取，使异步下载完成后头像即时出现（无需整列表重建）。
    private void InvalidateAvatar()
    {
        _avatarImage = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImage)));
    }

    private BitmapImage? LoadAvatarImage()
    {
        var localPath = AvatarPath;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            try
            {
                var mtime = File.GetLastWriteTimeUtc(localPath);
                if (AvatarCache.TryGetValue(localPath, out var cached) && cached.Mtime == mtime)
                {
                    return cached.Image;
                }

                // 从字节解码而非 new BitmapImage(Uri)：后者会长期持有文件句柄，导致删除账号时头像删不掉。
                // 用 FileShare.ReadWrite 共享读：即便另一线程正在替换该头像文件，也不抛共享冲突导致头像闪失。
                byte[] bytes;
                using (var fileStream = new FileStream(
                    localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fileStream.Length];
                    fileStream.ReadExactly(bytes);
                }
                var bitmap = new BitmapImage { DecodePixelWidth = AvatarDecodePixelWidth };
                using (var stream = new MemoryStream(bytes))
                {
                    // SetSource 同步消费完流后才返回，故 using 释放时机安全。
                    bitmap.SetSource(stream.AsRandomAccessStream());
                }

                AvatarCache[localPath] = (mtime, bitmap);
                return bitmap;
            }
            catch (Exception ex)
            {
                // 兜底 catch：损坏/非图片文件让 SetSource 抛 COMException，而本 getter 由 x:Bind 在
                // UI 线程直接调用，异常逃逸会崩掉整个应用；记日志后落到 URL 回退/默认头像。
                AppLog.Warn($"加载缓存账号头像失败：{localPath}，{ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return new BitmapImage(avatarUri);
        }

        return null;
    }

    // ---------- 列表多选 / 悬停的瞬时 UI 状态（不持久化，仅驱动卡片视觉，列表重建后由页面重新套用） ----------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;

    /// <summary>是否被勾选进批量选择集。</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            RaiseSelectionVisuals();
        }
    }

    private bool _isPointerOver;

    /// <summary>鼠标是否悬停在卡片上（用于悬停时才显示勾选框）。</summary>
    [JsonIgnore]
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (_isPointerOver == value)
            {
                return;
            }

            _isPointerOver = value;
            RaiseSelectionVisuals();
        }
    }

    /// <summary>选中时显示：整卡黑框 + 左上角实心对勾。</summary>
    [JsonIgnore]
    public Visibility SelectionRingVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>左上角勾选指示器：悬停或已选时出现。</summary>
    [JsonIgnore]
    public Visibility CheckIndicatorVisibility =>
        _isSelected || _isPointerOver ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>未选中时（指示器可见即仅悬停场景）显示空心圈。</summary>
    [JsonIgnore]
    public Visibility EmptyCheckCircleVisibility => _isSelected ? Visibility.Collapsed : Visibility.Visible;

    private void RaiseSelectionVisuals()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        handler(this, new PropertyChangedEventArgs(nameof(SelectionRingVisibility)));
        handler(this, new PropertyChangedEventArgs(nameof(CheckIndicatorVisibility)));
        handler(this, new PropertyChangedEventArgs(nameof(EmptyCheckCircleVisibility)));
    }
}
