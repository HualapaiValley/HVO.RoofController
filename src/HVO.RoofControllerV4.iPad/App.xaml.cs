using HVO.RoofControllerV4.iPad.ViewModels;
using Microsoft.Maui.ApplicationModel;

namespace HVO.RoofControllerV4.iPad;

public partial class App : Application
{
	private readonly AppShell _appShell;
	private readonly RoofControllerViewModel _viewModel;

	public App(AppShell appShell, RoofControllerViewModel viewModel)
	{
		InitializeComponent();
		UserAppTheme = AppTheme.Dark;
		_appShell = appShell ?? throw new ArgumentNullException(nameof(appShell));
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(_appShell);

		// Lease renewal only runs while the app is in the foreground; a backgrounded app lets the lease expire.
		window.Stopped += (_, _) => _viewModel.SetAppInForeground(false);
		window.Resumed += (_, _) => _viewModel.SetAppInForeground(true);
		window.Activated += (_, _) => _viewModel.SetAppInForeground(true);

		return window;
	}
}
