using Foundation;
using UIKit;

namespace HVO.RoofControllerV4.iPad;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication app, NSDictionary options)
	{
		var launched = base.FinishedLaunching(app, options);
		ConfigureGlobalAppearance();
		return launched;
	}

	private static void ConfigureGlobalAppearance()
	{
		var backgroundColor = UIColor.FromRGB(0x10, 0x13, 0x1A);
		var selectedColor = UIColor.FromRGB(0xF8, 0xF9, 0xFA);
		var unselectedColor = UIColor.FromRGB(0x6C, 0x75, 0x7D);

		var tabAppearance = new UITabBarAppearance();
		tabAppearance.ConfigureWithOpaqueBackground();
		tabAppearance.BackgroundColor = backgroundColor;
		tabAppearance.StackedLayoutAppearance.Selected.IconColor = selectedColor;
		tabAppearance.StackedLayoutAppearance.Selected.TitleTextAttributes = new UIStringAttributes { ForegroundColor = selectedColor };
		tabAppearance.StackedLayoutAppearance.Normal.IconColor = unselectedColor;
		tabAppearance.StackedLayoutAppearance.Normal.TitleTextAttributes = new UIStringAttributes { ForegroundColor = unselectedColor };

		UITabBar.Appearance.StandardAppearance = tabAppearance;
		if (OperatingSystem.IsIOSVersionAtLeast(15))
		{
			UITabBar.Appearance.ScrollEdgeAppearance = tabAppearance;
		}

		UITabBar.Appearance.TintColor = selectedColor;
		UITabBar.Appearance.UnselectedItemTintColor = unselectedColor;

		var navigationAppearance = new UINavigationBarAppearance();
		navigationAppearance.ConfigureWithOpaqueBackground();
		navigationAppearance.BackgroundColor = backgroundColor;
		navigationAppearance.TitleTextAttributes = new UIStringAttributes { ForegroundColor = selectedColor };

		UINavigationBar.Appearance.StandardAppearance = navigationAppearance;
		UINavigationBar.Appearance.ScrollEdgeAppearance = navigationAppearance;
		UINavigationBar.Appearance.TintColor = selectedColor;
	}
}
