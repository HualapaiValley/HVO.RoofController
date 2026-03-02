using System.IO;
using System.Reflection;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using HVO.RoofControllerV4.iPad.Configuration;
using HVO.RoofControllerV4.iPad.Popups;
using HVO.RoofControllerV4.iPad.Services;
using HVO.RoofControllerV4.iPad.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;

namespace HVO.RoofControllerV4.iPad;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("material-symbols-outlined-latin-100-normal.ttf", "MaterialSymbolsOutlined100");
				fonts.AddFont("material-symbols-outlined-latin-200-normal.ttf", "MaterialSymbolsOutlined200");
				fonts.AddFont("material-symbols-outlined-latin-300-normal.ttf", "MaterialSymbolsOutlined300");
				fonts.AddFont("material-symbols-outlined-latin-400-normal.ttf", "MaterialSymbolsOutlined400");
				fonts.AddFont("material-symbols-outlined-latin-500-normal.ttf", "MaterialSymbolsOutlined500");
				fonts.AddFont("material-symbols-outlined-latin-600-normal.ttf", "MaterialSymbolsOutlined600");
				fonts.AddFont("material-symbols-outlined-latin-700-normal.ttf", "MaterialSymbolsOutlined700");
			});

		AddConfiguration(builder);
		ConfigureServices(builder.Services, builder.Configuration);

#if DEBUG
		builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
		builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif

		builder.Logging.AddConsole();
		builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
		builder.Logging.AddFilter("System.Net.Http", LogLevel.Information);

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static void AddConfiguration(MauiAppBuilder builder)
	{
		var assembly = Assembly.GetExecutingAssembly();
	var resourceName = "HVO.RoofControllerV4.iPad.appsettings.json";
		using var stream = assembly.GetManifestResourceStream(resourceName);
		if (stream is not null)
		{
			((IConfigurationBuilder)builder.Configuration).AddJsonStream(stream);
		}

		var userConfigurationPath = Path.Combine(FileSystem.AppDataDirectory, "roofcontroller.settings.json");
		if (File.Exists(userConfigurationPath))
		{
			((IConfigurationBuilder)builder.Configuration).AddJsonFile(userConfigurationPath, optional: true, reloadOnChange: false);
		}
	}

	private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		services
			.AddOptions<RoofControllerApiOptions>()
			.Bind(configuration.GetSection("RoofControllerApi"))
			.ValidateDataAnnotations()
			.ValidateOnStart();

	services.AddHttpClient<IRoofControllerApiClient, RoofControllerApiClient>();
	services.AddSingleton<IDialogService, DialogService>();
	services.AddSingleton<IPopupService, PopupService>();
	services.AddSingleton<IPopupLifecycleController, PopupLifecycleController>();
	services.AddSingleton<IRoofControllerConfigurationService, RoofControllerConfigurationService>();
	services.AddTransientPopup<HealthStatusPopup, HealthStatusPopupViewModel>();

		services.AddSingleton<RoofControllerViewModel>();
		services.AddSingleton<MainPage>();
		services.AddSingleton<CameraPage>();
		services.AddSingleton<HistoryPage>();
		services.AddSingleton<ConfigurationPage>();
		services.AddSingleton<AppShell>();
	}
}
