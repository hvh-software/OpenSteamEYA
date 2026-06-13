using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SteamEyaWinUI.Services;

internal static class FormatHelper
{
    /// <summary>修正令牌头部常见的大小写粘贴错误（EyAi → eyAi）。</summary>
    public static string NormalizeToken(string token)
    {
        return token.Replace(
            "EyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0",
            "eyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0",
            StringComparison.Ordinal);
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        return $"{Math.Floor(remaining.TotalDays)} 天 {remaining.Hours} 小时 {remaining.Minutes} 分钟";
    }

    public static string FormatDateTime(DateTimeOffset value)
    {
        return value == default
            ? "未知时间"
            : value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>优先分达到此等级才能打优先匹配。</summary>
    public const int MinimumPremierLevel = 10;

    public static string FormatDuration(uint seconds)
    {
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var parts = new List<string>();

        if (days > 0)
        {
            parts.Add($"{days}天");
        }

        if (hours > 0)
        {
            parts.Add($"{hours}小时");
        }

        if (minutes > 0)
        {
            parts.Add($"{minutes}分");
        }

        return parts.Count > 0 ? string.Join("", parts) : $"{seconds}秒";
    }

    // 已知的 GC 冷却原因码映射为可读文案；未知码保留 "原因 N"；
    // reason 为 null（GC 未给原因码）返回空串，由 FormatCooldownText 据此省略原因括号，
    // 保持历史侧"无原因码则不显示原因"的旧观感（而非误导性的"原因 0"）。
    public static string DescribePenaltyReason(uint? reason) => reason switch
    {
        null => "",
        7 => "放弃比赛",
        22 => "vaclive",
        _ => $"原因 {reason.Value}"
    };

    /// <param name="seconds">冷却剩余秒数；null 表示 GC 未下发（未知）。</param>
    /// <param name="unknownText">seconds 为 null 时的文案（两处调用方措辞不同，参数化）。</param>
    public static string FormatCooldownText(uint? seconds, uint? reason, string unknownText)
    {
        if (seconds is null)
        {
            return unknownText;
        }

        if (seconds == 0)
        {
            return "无";
        }

        var duration = FormatDuration(seconds.Value);
        var description = DescribePenaltyReason(reason);
        return description.Length > 0 ? $"{duration}（{description}）" : duration;
    }

    /// <param name="vacBanned">GC VAC 标记：null 未知 / 0 无 / 其他 有标记。</param>
    /// <param name="unknownText">vacBanned 为 null 时的文案。</param>
    public static string FormatGcVacText(int? vacBanned, string unknownText) => vacBanned switch
    {
        null => unknownText,
        0 => "无",
        _ => "有标记"
    };

    public static string FormatCooldownStatusText(
        uint? seconds,
        uint? reason,
        int? vacBanned,
        string cooldownUnknownText,
        string vacUnknownText) =>
        $"冷却：{FormatCooldownText(seconds, reason, cooldownUnknownText)}；" +
        $"GC VAC：{FormatGcVacText(vacBanned, vacUnknownText)}";

    /// <param name="level">CS 玩家等级；null 时返回 <paramref name="unknownText"/>。</param>
    public static string FormatPlayerLevelText(int? level, string unknownText)
    {
        if (!level.HasValue)
        {
            return unknownText;
        }

        var status = level.Value >= MinimumPremierLevel
            ? "可打优先"
            : $"未达 {MinimumPremierLevel} 级";

        return $"{level.Value} 级（{status}）";
    }

    public static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "未知大小";
        }

        return bytes.Value >= 1024 * 1024
            ? $"{bytes.Value / 1024d / 1024d:F2} MB"
            : $"{bytes.Value / 1024d:F0} KB";
    }

    public static Brush GetStatusBrush(InfoBarSeverity severity)
    {
        var resourceKey = severity switch
        {
            InfoBarSeverity.Success => "SystemFillColorSuccessBrush",
            InfoBarSeverity.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush"
        };

        if (Application.Current.Resources.TryGetValue(resourceKey, out var resource) &&
            resource is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
