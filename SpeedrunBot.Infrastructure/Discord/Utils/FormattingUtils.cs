namespace SpeedrunBot.Infrastructure.Discord.Utils;

public static class FormattingUtils
{
    // Accurate Millisecond Formatting (00:00:00.000)
    public static string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ?
            $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}" :
            $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}