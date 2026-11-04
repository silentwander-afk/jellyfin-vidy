using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Vidy.Core;

public static class TitleMatcher
{
    private static readonly Regex TrailingYear =
        new(@"^(?<t>.+?)\s*\(?\b(?<y>19\d{2}|20[0-3]\d)\b\)?$", RegexOptions.Compiled);

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// "Dune 2021" or "Dune (2021)" → ("Dune", 2021). A bare number ("1917") or a number that is not a
    /// plausible year ("Blade Runner 2049") is left alone.
    /// </summary>
    public static (string Query, int? Year) SplitYear(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        var m = TrailingYear.Match(q);
        if (m.Success && m.Groups["t"].Value.Trim().Length > 0)
        {
            return (m.Groups["t"].Value.Trim(), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        return (q, null);
    }

    public static double Score(string query, string? title, string? originalTitle, int? queryYear, int? resultYear)
    {
        var q = Normalize(query);
        double best = 0;
        foreach (var candidate in new[] { title, originalTitle })
        {
            var c = Normalize(candidate);
            if (c.Length == 0 || q.Length == 0)
            {
                continue;
            }

            double s;
            if (c == q)
            {
                s = 1.0;
            }
            else if (c.StartsWith(q, StringComparison.Ordinal))
            {
                s = 0.85;
            }
            else if (c.Contains(q, StringComparison.Ordinal))
            {
                s = 0.7;
            }
            else
            {
                var dist = Levenshtein(q, c);
                s = Math.Max(0, 1.0 - (double)dist / Math.Max(q.Length, c.Length)) * 0.6;
            }

            best = Math.Max(best, s);
        }

        if (queryYear.HasValue && resultYear == queryYear)
        {
            best = Math.Min(1.2, best + 0.15);
        }

        return best;
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
