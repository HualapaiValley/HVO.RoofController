using Avalonia.Controls;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Build identification and the controller's summary (#209), available even while locked or disconnected: the summary is
/// read with the kiosk's own key, and grants no controller permissions.
/// </summary>
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
        if (!view.IsStale && !view.IsUnreachable)
        {
            // Where people reach the web UI first, as plainly as the version: from the host's address, or only on the
            // controller's machine, never a container's own address (#209). Then the summary's other rows, muted.
            foreach (var (label, value) in _system.Summary.Where(row => row.Label != VersionLabel))
            {
                var webUi = label == WebUiLabel;
                var row = KioskTheme.Label($"{label}: {value}", _metrics.Font, webUi ? null : KioskTheme.Muted);
                row.Name = webUi ? "about-web-ui" : $"about-summary-{label.ToLowerInvariant()}";
                page.Children.Add(row);
            }
        }
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

    /// <summary>The summary's row for the controller's version (<see cref="RoofSystemText.DescribeSummary"/>).</summary>
    private const string VersionLabel = "Controller";

    /// <summary>The summary's row for where people reach the web UI (<see cref="RoofSystemText.DescribeWebUi"/>).</summary>
    private const string WebUiLabel = "Web UI";

    private string ControllerText(KioskView view)
    {
        if (view.IsUnreachable)
        {
            return $"Controller version: unavailable. {RoofSystemText.ControllerUnreachable}";
        }
        if (view.IsStale)
        {
            return "Controller version: unavailable (controller status is not live).";
        }
        var version = _system.Summary.FirstOrDefault(row => row.Label == VersionLabel).Value;
        return !string.IsNullOrWhiteSpace(version) ? $"Controller (last read): {version}"
            : _system.SummaryProblem is { } problem ? $"Controller version: unavailable. {problem}"
            : "Controller version: reading…";
    }
}
