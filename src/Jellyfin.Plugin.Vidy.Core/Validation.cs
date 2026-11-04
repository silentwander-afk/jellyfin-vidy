using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Vidy.Core;

/// <summary>Pure helpers used to sanitise values coming from the Dashboard before they are used.</summary>
public static class Validation
{
    private static readonly Regex HexColor = new("^[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static readonly Regex Language = new("^[a-z]{2}(-[A-Z]{2})?$", RegexOptions.Compiled);
    private static readonly string[] Aspects = { "16:9", "4:3", "21:9" };

    public static int ClampInt(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    public static string NormalizeColor(string? value, string fallback = "DC2626")
    {
        var v = (value ?? string.Empty).Trim().TrimStart('#');
        return HexColor.IsMatch(v) ? v.ToUpperInvariant() : fallback;
    }

    public static string NormalizeLanguage(string? value, string fallback = "en-US")
    {
        var v = (value ?? string.Empty).Trim();
        return Language.IsMatch(v) ? v : fallback;
    }

    public static string NormalizeAspect(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return Array.IndexOf(Aspects, v) >= 0 ? v : "16:9";
    }

    public static string NormalizeApiKey(string? value) => (value ?? string.Empty).Trim();

    public static bool IsValidPosterPath(string? path) => path != null && Regex.IsMatch(path, @"^/[A-Za-z0-9_\-\.]{1,100}$");
}
