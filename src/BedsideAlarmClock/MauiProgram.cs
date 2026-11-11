using Microsoft.Extensions.Logging;
using BedsideAlarmClock.Display;
using BedsideAlarmClock.Platforms.Android;

namespace BedsideAlarmClock;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("NotoSerif.ttf", "ClockSerif");
				fonts.AddFont("NotoSansMono.ttf", "ClockMonospace");
			});
		builder.Services.AddSingleton<ClockBrightness>();
		builder.Services.AddSingleton<IClockBrightness>(services => services.GetRequiredService<ClockBrightness>());

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
