namespace BedsideAlarmClock;

internal static class ClockSettings
{
    public const string ShowSecondsKey = "clock.showSeconds";
    public const string Use24HourTimeKey = "clock.use24HourTime";
    public const string ShowDateKey = "clock.showDate";
    public const string ShowNextAlarmKey = "clock.showNextAlarm";
    public const string ClockFontKey = "clock.font";
    public const string ClockColorKey = "clock.color";

    public static readonly string[] ClockFonts = ["Sans serif", "Serif", "Monospace"];
    public static readonly string[] ClockColors = ["Grey", "Red", "Amber", "Green", "Blue"];
    private static readonly (string Time, string Date) GreyColorValues = ("#D8D8D8", "#858585");

    public static (string Time, string Date) ColorValues(string color) => color switch
    {
        "Grey" => GreyColorValues,
        "Red" => ("#D80000", "#850000"),
        "Amber" => ("#D88000", "#855000"),
        "Green" => ("#00C800", "#007C00"),
        "Blue" => ("#0080D8", "#005085"),
        _ => GreyColorValues
    };

    public static string FontFamily(string font) => font switch
    {
        "Serif" => "ClockSerif",
        "Monospace" => "ClockMonospace",
        _ => "sans-serif"
    };
}
