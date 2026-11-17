using Microsoft.Extensions.Logging;
using BedsideAlarmClock.Display;
using BedsideAlarmClock.Alarms;
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
		builder.Services.AddSingleton(_ => new AlarmRepository(Path.Combine(FileSystem.AppDataDirectory, "alarms.json")));
		builder.Services.AddSingleton(_ => new AlarmRegistrationRepository(Path.Combine(FileSystem.AppDataDirectory, "registrations.json")));
		builder.Services.AddSingleton(_ => new AndroidAlarmScheduler(
			Android.App.Application.Context, typeof(AlarmDeliveryReceiver), typeof(MainActivity)));
		builder.Services.AddSingleton(services => new AlarmRegistrationReconciler(
			services.GetRequiredService<AlarmRegistrationRepository>(),
			services.GetRequiredService<AndroidAlarmScheduler>(), TimeProvider.System));
		builder.Services.AddSingleton(services => new AlarmCoordinator(
			services.GetRequiredService<AlarmRepository>(),
			services.GetRequiredService<AlarmRegistrationReconciler>(), () => TimeZoneInfo.Local));
#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
