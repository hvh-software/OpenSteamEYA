using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Models;

// partial：实例会作为 ListView ItemsSource 跨越 WinRT ABI，需要 CsWinRT 源生成 vtable（AOT）。
public sealed partial class SteamAccountHistoryItem : INotifyPropertyChanged
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string EyaToken { get; set; } = "";

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

    public DateTimeOffset LastLoginAt { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public string? CompetitiveScore { get; set; }

    public string? AccountStatus { get; set; }

    public bool? JwtAvailable { get; set; }

    public string? JwtStatus { get; set; }

    public DateTimeOffset? JwtValidatedAt { get; set; }

    public int? PremierScore { get; set; }

    public int? PremierWins { get; set; }

    public DateTimeOffset? PremierScoreUpdatedAt { get; set; }

    public uint? CooldownSeconds { get; set; }

    public uint? CooldownReason { get; set; }

    public bool? GcVacBanned { get; set; }

    public int? CsPlayerLevel { get; set; }

    public bool? InCsMatch { get; set; }

    public DateTimeOffset? CsStatusUpdatedAt { get; set; }

    private string? _note;

    /// <summary>用户备注（可为空）。跟随账号持久化，可在详情面板编辑，并计入搜索。</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value;
            RaiseNoteVisuals();
        }
    }

    /// <summary>所属分组的稳定 ID 列表（分组定义存于设置，改名不影响此处）。始终非 null。</summary>
    public List<string> GroupIds { get; set; } = [];

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? Loc.T("Account_Title_Unnamed") : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? Loc.T("Account_Persona_NotSynced") : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? Loc.T("Account_Steam64_Unresolved") : SteamId;

    [JsonIgnore]
    public string LastLoginText => FormatHelper.FormatDateTime(LastLoginAt);

    [JsonIgnore]
    public string LastLoginShortText => LastLoginAt == default
        ? Loc.T("Account_LastLogin_Unknown")
        : LastLoginAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string LastLoginCaptionText => Loc.Tf("Account_LastLogin_Caption_Format", LastLoginShortText);

    [JsonIgnore]
    public string TokenExpiresText => TokenExpiresAt.HasValue
        ? FormatHelper.FormatDateTime(TokenExpiresAt.Value)
        : Loc.T("Account_TokenExpires_Unresolved");

    [JsonIgnore]
    public string CompetitiveScoreText
    {
        get
        {
            if (PremierScore.HasValue)
            {
                return PremierWins.HasValue
                    ? Loc.Tf("Account_Score_WithWins_Format", PremierScore.Value, PremierWins.Value)
                    : string.Format("{0:N0}", PremierScore.Value);
            }

            return string.IsNullOrWhiteSpace(CompetitiveScore) ? Loc.T("Account_Score_Pending") : CompetitiveScore;
        }
    }

    // GcVacBanned 是 bool?，转成 FormatHelper 的 int? 约定（null 未知 / 0 无 / 非 0 有标记）。
    private int? GcVacBannedAsInt => GcVacBanned.HasValue ? (GcVacBanned.Value ? 1 : 0) : null;

    [JsonIgnore]
    public string CooldownText => FormatHelper.FormatCooldownText(CooldownSeconds, CooldownReason, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string CooldownSummaryText => Loc.Tf("Account_Cooldown_Summary_Format", CooldownText);

    [JsonIgnore]
    public string GcVacText => FormatHelper.FormatGcVacText(GcVacBannedAsInt, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(CooldownSeconds, CooldownReason, GcVacBannedAsInt, Loc.T("Account_Pending"), Loc.T("Account_Pending"));

    // ---------- 冷却倒计时（快照剩余秒 − 已流逝时间，实时递减；由页面每秒计时器驱动刷新绑定） ----------

    /// <summary>冷却锚点：查询写入冷却快照的时刻，用于扣除已流逝时间。</summary>
    private DateTimeOffset? CooldownAnchor => CsStatusUpdatedAt ?? PremierScoreUpdatedAt;

    /// <summary>
    /// 实时剩余冷却秒数。语义与 <see cref="CooldownSeconds"/> 一致：null=未知（GC 未答）/ 0=无冷却 / 正=剩余秒。
    /// 仅当快照为 (0, int.MaxValue] 且有锚点时才扣除流逝；否则原样透传，交由格式化按未知/无冷却判定。
    /// </summary>
    [JsonIgnore]
    public uint? RemainingCooldownSeconds
    {
        get
        {
            var snapshot = CooldownSeconds;
            if (snapshot is null or 0 || snapshot > int.MaxValue)
            {
                return snapshot;
            }

            var anchor = CooldownAnchor;
            if (anchor is null)
            {
                return snapshot;
            }

            var remaining = snapshot.Value - (DateTimeOffset.Now - anchor.Value).TotalSeconds;
            return remaining <= 0 ? 0u : (uint)remaining;
        }
    }

    /// <summary>是否仍在倒计时（页面据此决定是否继续每秒刷新该卡片，全部到期后即可停表）。</summary>
    [JsonIgnore]
    public bool HasLiveCooldown => CooldownAnchor is not null && RemainingCooldownSeconds is > 0 and <= int.MaxValue;

    [JsonIgnore]
    public string RemainingCooldownText =>
        FormatHelper.FormatCooldownCountdownText(RemainingCooldownSeconds, CooldownReason, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string RemainingCooldownSummaryText => Loc.Tf("Account_Cooldown_Summary_Format", RemainingCooldownText);

    [JsonIgnore]
    public string RemainingCooldownStatusText => Loc.Tf(
        "Format_CooldownStatus_Format",
        RemainingCooldownText,
        FormatHelper.FormatGcVacText(GcVacBannedAsInt, Loc.T("Account_Pending")));

    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    [JsonIgnore]
    public Visibility NoteIndicatorVisibility => HasNote ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string CsPlayerLevelText => FormatHelper.FormatPlayerLevelText(CsPlayerLevel, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string InCsMatchText => InCsMatch.HasValue
        ? (InCsMatch.Value ? Loc.T("Account_InMatch_Maybe") : Loc.T("Account_InMatch_None"))
        : Loc.T("Account_Pending");

    [JsonIgnore]
    public string AccountStatusText
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(AccountStatus) ? Loc.T("Account_Pending") : AccountStatus;
            var updatedAt = CsStatusUpdatedAt ?? PremierScoreUpdatedAt;
            if (!updatedAt.HasValue)
            {
                return status;
            }

            return Loc.Tf("Account_Status_WithTime_Format", status, FormatHelper.FormatDateTime(updatedAt.Value));
        }
    }

    [JsonIgnore]
    public string JwtAvailabilityText
    {
        get
        {
            var status = JwtAvailable.HasValue
                ? (JwtAvailable.Value ? Loc.T("Account_Jwt_Valid") : Loc.T("Account_Jwt_Invalid"))
                : JwtStatus;

            if (string.IsNullOrWhiteSpace(status))
            {
                return Loc.T("Account_Pending");
            }

            return JwtValidatedAt.HasValue
                ? Loc.Tf("Account_Status_WithTime_Format", status, FormatHelper.FormatDateTime(JwtValidatedAt.Value))
                : status;
        }
    }

    // 进程级头像缓存：列表重建会换新实例，仅靠实例字段无法跨重建复用，必须用静态字典才真正止血。
    // 键 = 完整路径，值带最后写入时间：mtime 不符就重新解码并「替换」同键条目——每次刷新都会重写头像文件
    //（mtime 必变），若把 mtime 编进键，旧解码位图会永久滞留字典（每轮刷新 × 每账号泄漏一份）。
    // 仅 UI 线程访问（BitmapImage 也只能在 UI 线程使用），普通 Dictionary 即可。
    private static readonly Dictionary<string, (DateTime Mtime, BitmapImage Image)> AvatarCache =
        new(StringComparer.OrdinalIgnoreCase);

    // PersonPicture 最大显示 92px（历史详情），按 2 倍留 DPI 余量解码。
    private const int AvatarDecodePixelWidth = 184;

    private BitmapImage? _avatarImage;

    [JsonIgnore]
    public BitmapImage? AvatarImage
    {
        get
        {
            if (_avatarImage is not null)
            {
                return _avatarImage;
            }

            _avatarImage = LoadAvatarImage();
            return _avatarImage;
        }
    }

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
                AppLog.Warn($"加载头像失败：{localPath}，{ex.Message}");
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

    /// <summary>由页面每秒计时器调用：通知倒计时相关绑定重取，实现卡片/详情实时递减。</summary>
    public void NotifyCooldownTick()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownText)));
        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownSummaryText)));
        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownStatusText)));
    }

    private void RaiseNoteVisuals()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(Note)));
        handler(this, new PropertyChangedEventArgs(nameof(HasNote)));
        handler(this, new PropertyChangedEventArgs(nameof(NoteIndicatorVisibility)));
    }
}
