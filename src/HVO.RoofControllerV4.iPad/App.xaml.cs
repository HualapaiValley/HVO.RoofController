using Microsoft.Maui.ApplicationModel;

namespace HVO.RoofControllerV4.iPad;

public partial class App : Application
{
	private readonly AppShell _appShell;

	public App(AppShell appShell)
	{
		InitializeComponent();
		UserAppTheme = AppTheme.Dark;
		_appShell = appShell ?? throw new ArgumentNullException(nameof(appShell));
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(_appShell);
	}
}