using System.Net;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

internal static partial class SteamLicensePageParser
{
    private const RegexOptions HtmlRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    /// <summary>
    /// 只在 Steam 授权表的条目名称列里识别 PW Grant。登录/错误页、结构不完整或
    /// 尚有下一页但本页未找到许可时返回未知，不能把它们误判为“无国服许可”。
    /// </summary>
    public static bool? HasSteamChinaPwGrant(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        try
        {
            var content = NonContentRegex().Replace(html, "");
            var recognized = false;
            var incomplete = false;

            foreach (Match table in TableRegex().Matches(content))
            {
                if (!HasClass(table.Groups["attributes"].Value, "account_table"))
                {
                    continue;
                }

                var body = table.Groups["body"].Value;
                var rows = RowRegex().Matches(body);
                var hasLicenseHeader = rows.Cast<Match>().Any(row =>
                    IsLicenseHeader(CellRegex().Matches(row.Groups["body"].Value)));
                if (!hasLicenseHeader)
                {
                    continue;
                }

                // 已知结构：account_table，三列表头/行（日期、条目、获取方式）。
                // 证据：SteamDB BrowserExtension 的 account_licenses.js、Playnite 的
                // SteamLicenseService.cs，以及 winnow 的 SteamLicensesPageParser.cs。
                recognized = true;
                incomplete |= OpenRowRegex().Matches(body).Count != rows.Count;

                foreach (Match row in rows)
                {
                    var cells = CellRegex().Matches(row.Groups["body"].Value);
                    if (IsLicenseHeader(cells))
                    {
                        continue;
                    }

                    if (cells.Count != 3 ||
                        cells.Cast<Match>().Any(cell => !IsTag(cell, "td")) ||
                        !HasClass(cells[0].Groups["attributes"].Value, "license_date_col") ||
                        !HasClass(cells[2].Groups["attributes"].Value, "license_acquisition_col"))
                    {
                        incomplete = true;
                        continue;
                    }

                    var name = ReadLicenseName(cells[1].Groups["body"].Value);
                    if (name.Length == 0)
                    {
                        incomplete = true;
                    }
                    else if (name.Equals("Steam China PW Grant", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            if (!recognized || incomplete)
            {
                return null;
            }

            foreach (Match anchor in AnchorRegex().Matches(content))
            {
                if (HasClass(anchor.Groups["attributes"].Value, "license_paginator_next"))
                {
                    return null;
                }
            }

            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static bool IsLicenseHeader(MatchCollection cells) =>
        cells.Count == 3 &&
        cells.Cast<Match>().All(cell => IsTag(cell, "th")) &&
        HasClass(cells[0].Groups["attributes"].Value, "license_date_col") &&
        HasClass(cells[2].Groups["attributes"].Value, "license_acquisition_col");

    private static bool IsTag(Match cell, string tag) =>
        cell.Groups["tag"].Value.Equals(tag, StringComparison.OrdinalIgnoreCase);

    private static bool HasClass(string attributes, string expected)
    {
        foreach (Match attribute in AttributeRegex().Matches(attributes))
        {
            if (!attribute.Groups["name"].Value.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value :
                attribute.Groups["single"].Success ? attribute.Groups["single"].Value :
                attribute.Groups["unquoted"].Value;
            return WebUtility.HtmlDecode(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains(expected, StringComparer.Ordinal);
        }

        return false;
    }

    private static string ReadLicenseName(string html)
    {
        var withoutControls = DivRegex().Replace(html, match =>
            HasClass(match.Groups["attributes"].Value, "free_license_remove_link") ? "" : match.Value);
        var text = WebUtility.HtmlDecode(TagRegex().Replace(withoutControls, ""));
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex("""<!--.*?-->|<(?:script|style)\b(?:[^"'<>]|"[^"]*"|'[^']*')*>.*?</(?:script|style)\s*>""", HtmlRegexOptions, 2000)]
    private static partial Regex NonContentRegex();

    [GeneratedRegex("""<table\b(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>(?<body>.*?)</table\s*>""", HtmlRegexOptions, 2000)]
    private static partial Regex TableRegex();

    [GeneratedRegex("""<tr\b(?:[^"'<>]|"[^"]*"|'[^']*')*>(?<body>.*?)</tr\s*>""", HtmlRegexOptions, 2000)]
    private static partial Regex RowRegex();

    [GeneratedRegex("""<tr\b(?:[^"'<>]|"[^"]*"|'[^']*')*>""", HtmlRegexOptions, 2000)]
    private static partial Regex OpenRowRegex();

    [GeneratedRegex("""<(?<tag>td|th)\b(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>(?<body>.*?)</\k<tag>\s*>""", HtmlRegexOptions, 2000)]
    private static partial Regex CellRegex();

    [GeneratedRegex("""<a\b(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>""", HtmlRegexOptions, 2000)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("""(?:^|\s)(?<name>[^\s=/>]+)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<unquoted>[^\s"'=<>`]+))""", HtmlRegexOptions, 2000)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("""<div\b(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>(?<body>.*?)</div\s*>""", HtmlRegexOptions, 2000)]
    private static partial Regex DivRegex();

    [GeneratedRegex("""<(?:[^"'<>]|"[^"]*"|'[^']*')*>""", HtmlRegexOptions, 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex WhitespaceRegex();
}
