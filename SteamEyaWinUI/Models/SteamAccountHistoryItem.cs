using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Models;

// partial：实例会作为 ListView ItemsSource 跨越 WinRT ABI，需要 CsWinRT 源生成 vtable（AOT）。
public sealed partial class SteamAccountHistoryItem
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string EyaToken { get; set; } = "";

    public string? PersonaName { get; set; }

    public string? AvatarUrl { get; set; }

    public string? AvatarPath { get; set; }

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

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? "未命名账号" : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? "Steam 资料未同步" : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? "Steam64 未解析" : SteamId;

    [JsonIgnore]
    public string LastLoginText => FormatHelper.FormatDateTime(LastLoginAt);

    [JsonIgnore]
    public string LastLoginShortText => LastLoginAt == default
        ? "未知"
        : LastLoginAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string TokenExpiresText => TokenExpiresAt.HasValue
        ? FormatHelper.FormatDateTime(TokenExpiresAt.Value)
        : "未解析";

    [JsonIgnore]
    public string CompetitiveScoreText
    {
        get
        {
            if (PremierScore.HasValue)
            {
                return PremierWins.HasValue
                    ? $"{PremierScore.Value:N0}（胜场 {PremierWins.Value}）"
                    : $"{PremierScore.Value:N0}";
            }

            return string.IsNullOrWhiteSpace(CompetitiveScore) ? "待查询" : CompetitiveScore;
        }
    }

    // GcVacBanned 是 bool?，转成 FormatHelper 的 int? 约定（null 未知 / 0 无 / 非 0 有标记）。
    private int? GcVacBannedAsInt => GcVacBanned.HasValue ? (GcVacBanned.Value ? 1 : 0) : null;

    [JsonIgnore]
    public string CooldownText => FormatHelper.FormatCooldownText(CooldownSeconds, CooldownReason, "待查询");

    [JsonIgnore]
    public string CooldownSummaryText => $"冷却：{CooldownText}";

    [JsonIgnore]
    public string GcVacText => FormatHelper.FormatGcVacText(GcVacBannedAsInt, "待查询");

    [JsonIgnore]
    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(CooldownSeconds, CooldownReason, GcVacBannedAsInt, "待查询", "待查询");

    [JsonIgnore]
    public string CsPlayerLevelText => FormatHelper.FormatPlayerLevelText(CsPlayerLevel, "待查询");

    [JsonIgnore]
    public string InCsMatchText => InCsMatch.HasValue
        ? (InCsMatch.Value ? "可能在对局中" : "未发现对局")
        : "待查询";

    [JsonIgnore]
    public string AccountStatusText
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(AccountStatus) ? "待查询" : AccountStatus;
            var updatedAt = CsStatusUpdatedAt ?? PremierScoreUpdatedAt;
            if (!updatedAt.HasValue)
            {
                return status;
            }

            return $"{status}，{FormatHelper.FormatDateTime(updatedAt.Value)}";
        }
    }

    [JsonIgnore]
    public string JwtAvailabilityText
    {
        get
        {
            var status = JwtAvailable.HasValue
                ? (JwtAvailable.Value ? "有效" : "无效")
                : JwtStatus;

            if (string.IsNullOrWhiteSpace(status))
            {
                return "待查询";
            }

            return JwtValidatedAt.HasValue
                ? $"{status}，{FormatHelper.FormatDateTime(JwtValidatedAt.Value)}"
                : status;
        }
    }

    // 进程级头像缓存：列表重建会换新实例，仅靠实例字段无法跨重建复用，必须用静态字典才真正止血。
    // 键 = 完整路径 + 最后写入时间，头像更新（重新下载覆盖同名文件）后键变化自动失效。
    // 仅 UI 线程访问（BitmapImage 也只能在 UI 线程使用），普通 Dictionary 即可。
    private static readonly Dictionary<string, BitmapImage> AvatarCache = new(StringComparer.OrdinalIgnoreCase);

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

    private BitmapImage? LoadAvatarImage()
    {
        var localPath = AvatarPath;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            try
            {
                var cacheKey = $"{localPath}|{File.GetLastWriteTimeUtc(localPath):O}";
                if (AvatarCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                // 从字节解码而非 new BitmapImage(Uri)：后者会长期持有文件句柄，导致删除账号时头像删不掉。
                var bytes = File.ReadAllBytes(localPath);
                var bitmap = new BitmapImage { DecodePixelWidth = AvatarDecodePixelWidth };
                using (var stream = new MemoryStream(bytes))
                {
                    // SetSource 同步消费完流后才返回，故 using 释放时机安全。
                    bitmap.SetSource(stream.AsRandomAccessStream());
                }

                AvatarCache[cacheKey] = bitmap;
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"加载头像失败：{localPath}，{ex.Message}");
                // 落到下面的 URL 回退或返回 null（默认头像），与原行为一致。
            }
        }

        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return new BitmapImage(avatarUri);
        }

        return null;
    }
}
