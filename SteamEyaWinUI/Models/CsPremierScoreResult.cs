using SteamEyaWinUI.Localization;
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
    bool InMatch,
    bool? Cs2IsChina)
{
    public bool HasPremierScore => PremierRanking is not null && PremierRanking.RankId > 0;

    public bool HasCooldownData => PenaltySeconds.HasValue;

    // 上限 int.MaxValue：超过则是“负的剩余秒数”被当无符号读出的天文数字（冷却其实已过期），不算有冷却。
    public bool HasCooldown => PenaltySeconds is > 0 and <= int.MaxValue;

    public bool IsGcVacBanned => VacBanned is not (null or 0);

    /// <summary>写入历史记录用：未知保持 null，不能折叠成“无标记”。</summary>
    public bool? GcVacBannedOrUnknown => VacBanned is null ? null : VacBanned != 0;

    public string DisplayText => HasPremierScore
        ? Loc.Tf("Account_Score_WithWins_Format", PremierRanking!.RankId, PremierRanking.Wins)
        : Loc.T("Cs_Premier_NoScore");

    public string CooldownText => FormatHelper.FormatCooldownText(PenaltySeconds, PenaltyReason, Loc.T("Cs_Unknown_GcNoResponse"));

    public string GcVacText => FormatHelper.FormatGcVacText(VacBanned, Loc.T("Cs_Unknown"));

    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(PenaltySeconds, PenaltyReason, VacBanned, Loc.T("Cs_Unknown_GcNoResponse"), Loc.T("Cs_Unknown"));

    public string PlayerLevelText => FormatHelper.FormatPlayerLevelText(PlayerLevel, Loc.T("Cs_PlayerLevel_NotRead"));

    public string Cs2IsChinaText => Cs2IsChina switch
    {
        true => Loc.T("Cs2_IsChina_Yes"),
        false => Loc.T("Cs2_IsChina_No"),
        _ => Loc.T("Cs_Unknown")
    };

    public string StatusText
    {
        get
        {
            var restrictions = new List<string>();
            if (IsGcVacBanned)
            {
                restrictions.Add(Loc.T("Cs_Premier_Restriction_GcVac"));
            }

            if (HasCooldown)
            {
                var kind = PenaltySeconds > 365U * 86400U
                    ? Loc.T("Cs_Premier_Cooldown_LongBan")
                    : Loc.T("Cs_Premier_Cooldown_Normal");
                restrictions.Add(Loc.Tf("Cs_Premier_Restriction_Format", kind, CooldownText));
            }

            if (restrictions.Count > 0)
            {
                return string.Join(Loc.T("Cs_Premier_Restriction_Separator"), restrictions);
            }

            return HasCooldownData
                ? Loc.T("Cs_Premier_NoRestrictions")
                : Loc.T("Cs_Premier_CooldownUnknown");
        }
    }
}

public sealed record CsRankingInfo(
    uint RankTypeId,
    uint RankId,
    uint Wins,
    uint? MapId);
