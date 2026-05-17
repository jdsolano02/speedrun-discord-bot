namespace SpeedrunBot.Infrastructure.Discord.Utils;

public static class FormattingUtils
{
    // NEW: Define which keywords invalidate a run from receiving competitive prestige
    private static readonly string[] _nonMainBoardKeywords = { "Category Extension", "Meme", "Break Dirt" };

    public static bool IsEligibleForPrestige(string gameFullName, string categoryName)
    {
        if (string.IsNullOrWhiteSpace(gameFullName) || string.IsNullOrWhiteSpace(categoryName)) return true;

        return !_nonMainBoardKeywords.Any(keyword =>
            gameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            categoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    // Accurate Millisecond Formatting (00:00:00.000)
    public static string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ?
            $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}" :
            $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}