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
    private readonly KioskPager _document;

    public KioskAboutPage(KioskConsole console, KioskSystemPanel system, KioskMetrics metrics)
    {
        _console = console;
        _system = system;
        _metrics = metrics;
        _document = new KioskPager(metrics, "about-pages");
        system.Changed += () => Update(console.View);
        Update(console.View);
    }

    public void Update(KioskView view)
    {
        var page = new StackPanel { Spacing = _metrics.Gap * 2 };
        page.Children.Add(KioskTheme.Label("About", _metrics.Large, KioskTheme.Accent, weight: FontWeight.SemiBold));
        var self = _system.Self;
        var client = KioskTheme.Label($"{self.Label} version: {self.Value}", _metrics.Font);
        client.Name = "about-client-version";
        page.Children.Add(client);
        var controller = KioskTheme.Label(ControllerText(view), _metrics.Font);
        controller.Name = "about-controller-version";
        page.Children.Add(controller);
        page.Children.Add(KioskTheme.Label(
            _console.Wording.SignIn == KioskSignIn.Pin
                ? "PIN policy: every PIN session is Operator-only, including an administrator's PIN. Settings and System require password sign-in in the Mac, web or CLI client. A PIN grants no host or terminal privileges."
                : "Desktop permissions follow your password session. Settings and System retain the controller's role checks.", _metrics.Font, KioskTheme.Muted));
        if (_console.Wording.SignIn == KioskSignIn.Pin)
        {
            var items = page.Children.ToArray();
            page.Children.Clear();
            _document.SetItems(items);
            Content = _document;
        }
        else Content = KioskTheme.Card(page, _metrics);
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
