using System.IO;
using System.Reflection;
using CommunityToolkit.Maui;
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
	private const string SettingsFileName = "roofcontroller.settings.json";
	private const string StopHttpClientName = "RoofControllerStop";

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

		// A missing, unreadable or invalid settings file never stops the app: a bad file is moved aside and the
		// built-in defaults apply. The dashboard reports what happened.
		var settingsStore = new SettingsFileStore(Path.Combine(FileSystem.AppDataDirectory, SettingsFileName));
		var settingsLoad = RoofControllerSettingsLoader.Load(settingsStore);

		ConfigureServices(builder.Services, builder.Configuration, settingsStore, settingsLoad);

#if DEBUG
		builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
		builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif

		builder.Logging.AddConsole();
		builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
		builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

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
	}

	private static void ConfigureServices(IServiceCollection services, IConfiguration configuration, SettingsFileStore settingsStore, SettingsLoadResult settingsLoad)
	{
		services
			.AddOptions<RoofControllerApiOptions>()
			.Bind(configuration.GetSection(RoofControllerSettingsLoader.SectionName))
			.Configure(options =>
			{
				if (settingsLoad.Options is { } saved)
				{
					options.CopyFrom(saved);
				}
			})
			.ValidateDataAnnotations()
			.ValidateOnStart();

		services.AddSingleton(settingsStore);
		services.AddSingleton(settingsLoad);
		services.AddSingleton<IApiKeyStore, SecureStorageApiKeyStore>();
		services.AddSingleton<RoofControllerConnection>();
		services.AddSingleton(_ => new StatusOrderingGate());
		services.AddSingleton(_ => new LeaseRenewalTracker());

		services.AddHttpClient<IRoofControllerApiClient, RoofControllerApiClient>(client => client.Timeout = TimeSpan.FromSeconds(30));

		// Stop uses its own HttpClient (its own connection pool) so it never queues behind polls or other commands.
		services.AddHttpClient(StopHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
		services.AddSingleton(sp =>
		{
			var connection = sp.GetRequiredService<RoofControllerConnection>();
			var stopClient = new RoofControllerApiClient(
				sp.GetRequiredService<IHttpClientFactory>().CreateClient(StopHttpClientName),
				connection,
				sp.GetRequiredService<ILogger<RoofControllerApiClient>>());
			return new StopCommandCoordinator(connection, stopClient.StopAsync);
		});

		services.AddSingleton<IDialogService, DialogService>();
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
