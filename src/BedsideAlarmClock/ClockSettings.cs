namespace BedsideAlarmClock;

internal static class ClockSettings
{
    public const string ShowSecondsKey = "clock.showSeconds";
    public const string Use24HourTimeKey = "clock.use24HourTime";
    public const string ShowDateKey = "clock.showDate";
    public const string ClockFontKey = "clock.font";

    public static readonly string[] ClockFonts = ["Sans serif", "Serif", "Monospace"];

    public static string FontFamily(string font) => font switch
    {
        "Serif" => "ClockSerif",
        "Monospace" => "ClockMonospace",
        _ => "sans-serif"
    };
}
