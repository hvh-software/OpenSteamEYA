using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal enum HistoryBooleanFilter
{
    All,
    Yes,
    No,
    Unknown
}

internal enum HistoryScoreFilter
{
    All,
    HasScore,
    NoScore,
    Unknown
}

internal sealed record HistoryAccountFilter(
    HistoryBooleanFilter Vac = HistoryBooleanFilter.All,
    HistoryBooleanFilter China = HistoryBooleanFilter.All,
    HistoryScoreFilter Score = HistoryScoreFilter.All,
    int? MinScore = null,
    int? MaxScore = null)
{
    public bool IsValidScoreRange =>
        MinScore is not < 0 &&
        MaxScore is not < 0 &&
        !(MinScore is int min && MaxScore is int max && min > max);

    public bool Matches(SteamAccountHistoryItem account)
    {
        if (!IsValidScoreRange ||
            !MatchesBoolean(Vac, account.GcVacBanned) ||
            !MatchesBoolean(China, account.Cs2IsChina))
        {
            return false;
        }

        var score = account.PremierScore;
        // 查询成功但没有排名与尚未查询严格区分，不依赖已本地化的显示文案。
        var scoreState = score switch
        {
            > 0 => HistoryScoreFilter.HasScore,
            null or 0 when account.PremierScoreUpdatedAt.HasValue => HistoryScoreFilter.NoScore,
            _ => HistoryScoreFilter.Unknown
        };

        if (Score != HistoryScoreFilter.All && Score != scoreState)
        {
            return false;
        }

        if (MinScore is null && MaxScore is null)
        {
            return true;
        }

        // 设定任意分数边界时，只匹配实际有分的账号；上下界均包含端点。
        return score is int numericScore && numericScore > 0 &&
            (!MinScore.HasValue || numericScore >= MinScore.Value) &&
            (!MaxScore.HasValue || numericScore <= MaxScore.Value);
    }

    private static bool MatchesBoolean(HistoryBooleanFilter filter, bool? value) => filter switch
    {
        HistoryBooleanFilter.All => true,
        HistoryBooleanFilter.Yes => value == true,
        HistoryBooleanFilter.No => value == false,
        HistoryBooleanFilter.Unknown => value is null,
        _ => false
    };
}
