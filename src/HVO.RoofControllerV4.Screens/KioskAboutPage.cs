using Avalonia.Controls;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>Build identification available even while locked or disconnected; it grants no controller permissions.</summary>
public sealed class KioskAboutPage : UserControl
{
    private readonly KioskConsole _console;
    private readonly KioskSystemPanel _system;
    private readonly KioskMetrics _metrics;

    public KioskAboutPage(KioskConsole console, KioskSystemPanel system, KioskMetrics metrics)
    {
        _console = console;
        _system = system;
        _metrics = metrics;
        system.Changed += () => Update(console.View);
        Update(console.View);
    }

    public void Update(KioskView view)
    {
        var page = new StackPanel { Spacing = _metrics.Gap * 2 };
        page.Children.Add(KioskTheme.Label("About", _metrics.Large, weight: FontWeight.SemiBold));
        var self = _system.Self;
        var client = KioskTheme.Label($"{self.Label} version: {self.Value}", _metrics.Font);
        client.Name = "about-client-version";
        page.Children.Add(client);
        var controller = KioskTheme.Label(ControllerText(view), _metrics.Font);
        controller.Name = "about-controller-version";
        page.Children.Add(controller);
        Content = page;
    }

    private string ControllerText(KioskView view)
    {
        if (view.IsStale || view.IsUnreachable)
        {
            return "Controller version: unavailable (controller status is not live).";
        }
        if (_console.Wording.SignIn == KioskSignIn.Pin)
        {
            return "Controller version: unavailable in the PIN kiosk. Use an administrator session in CLI or web System.";
        }
        if (!view.IsAdmin)
        {
            return "Controller version: unavailable (requires an administrator session in System).";
        }
        var information = _system.Information.FirstOrDefault(row => row.Label == "Application").Value;
        return string.IsNullOrWhiteSpace(information)
            ? "Controller version: unavailable. Open System to read controller information."
            : $"Controller (last read): {information}";
    }
}
