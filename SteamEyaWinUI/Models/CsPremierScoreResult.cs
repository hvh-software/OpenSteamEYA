namespace SteamEyaWinUI.Models;

// 冷却三字段（PenaltySeconds/PenaltyReason/VacBanned）来自 GC 的 MatchmakingGC2ClientHello(9110)，
// null 表示 9110 未下发（冷却状态未知），与“确认无冷却”(0) 严格区分。
public sealed record CsPremierScoreResult(
    string SteamId,
    uint AccountId,
    CsRankingInfo? PremierRanking,
    IReadOnlyList<CsRankingInfo> Rankings,
    uint? PenaltySeconds,
    uint? PenaltyReason,
    int? VacBanned,
    int? PlayerLevel,
    bool InMatch)
{
    private const int MinimumPremierLevel = 10;

    public bool HasPremierScore => PremierRanking is not null && PremierRanking.RankId > 0;

    public bool HasCooldownData => PenaltySeconds.HasValue;

    public bool HasCooldown => PenaltySeconds is > 0;

    public bool IsGcVacBanned => VacBanned is not (null or 0);

    /// <summary>写入历史记录用：未知保持 null，不能折叠成“无标记”。</summary>
    public bool? GcVacBannedOrUnknown => VacBanned is null ? null : VacBanned != 0;

    public string DisplayText => HasPremierScore
        ? $"{PremierRanking!.RankId:N0}（胜场 {PremierRanking.Wins}）"
        : "暂无优先分";

    public string CooldownText => PenaltySeconds switch
    {
        null => "未知（GC 未响应）",
        0 => "无",
        _ => $"{FormatDuration(PenaltySeconds.Value)}（原因 {PenaltyReason ?? 0}）"
    };

    public string GcVacText => VacBanned switch
    {
        null => "未知",
        0 => "无",
        _ => "有标记"
    };

    public string CooldownStatusText => $"冷却：{CooldownText}；GC VAC：{GcVacText}";

    public string PlayerLevelText
    {
        get
        {
            if (!PlayerLevel.HasValue)
            {
                return "未读取";
            }

            var status = PlayerLevel.Value >= MinimumPremierLevel
                ? "可打优先"
                : $"未达 {MinimumPremierLevel} 级";

            return $"{PlayerLevel.Value} 级（{status}）";
        }
    }

    public string StatusText
    {
        get
        {
            var restrictions = new List<string>();
            if (IsGcVacBanned)
            {
                restrictions.Add("GC 标记 VAC");
            }

            if (HasCooldown)
            {
                var kind = PenaltySeconds > 365U * 86400U
                    ? "长期/永久竞技封禁"
                    : "竞技冷却";
                restrictions.Add($"{kind}：{CooldownText}");
            }

            if (restrictions.Count > 0)
            {
                return string.Join("；", restrictions);
            }

            return HasCooldownData
                ? "未发现 CS2 限制"
                : "冷却状态未知（GC 未响应，可稍后重试）";
        }
    }

    private static string FormatDuration(uint seconds)
    {
        if (seconds == 0)
        {
            return "无";
        }

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
}

public sealed record CsRankingInfo(
    uint RankTypeId,
    uint RankId,
    uint Wins,
    uint? MapId);
