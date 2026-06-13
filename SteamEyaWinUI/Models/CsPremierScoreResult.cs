using SteamEyaWinUI.Services;

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
    public bool HasPremierScore => PremierRanking is not null && PremierRanking.RankId > 0;

    public bool HasCooldownData => PenaltySeconds.HasValue;

    public bool HasCooldown => PenaltySeconds is > 0;

    public bool IsGcVacBanned => VacBanned is not (null or 0);

    /// <summary>写入历史记录用：未知保持 null，不能折叠成“无标记”。</summary>
    public bool? GcVacBannedOrUnknown => VacBanned is null ? null : VacBanned != 0;

    public string DisplayText => HasPremierScore
        ? $"{PremierRanking!.RankId:N0}（胜场 {PremierRanking.Wins}）"
        : "暂无优先分";

    public string CooldownText => FormatHelper.FormatCooldownText(PenaltySeconds, PenaltyReason, "未知（GC 未响应）");

    public string GcVacText => FormatHelper.FormatGcVacText(VacBanned, "未知");

    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(PenaltySeconds, PenaltyReason, VacBanned, "未知（GC 未响应）", "未知");

    public string PlayerLevelText => FormatHelper.FormatPlayerLevelText(PlayerLevel, "未读取");

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
}

public sealed record CsRankingInfo(
    uint RankTypeId,
    uint RankId,
    uint Wins,
    uint? MapId);
