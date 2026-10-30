using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The History page's contents (#206, #208): who unlocked the kiosk (or signed in to the app), as the controller knows
/// them, and their own most recent roof commands, newest first. Read with the person's session only; while locked there is
/// nothing to read, and nothing read stays after the kiosk locks.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskHistoryPanel
{
    /// <summary>How many of the person's commands the page reads.</summary>
    public const int Take = 20;

    private readonly KioskConsole _console;
    private CancellationTokenSource _reset = new();

    public KioskHistoryPanel(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The person, or null before they are read (or when they could not be).</summary>
    public RoofCallerResponse? Person { get; private set; }

    /// <summary>The person's recent commands, or null before they are read (or when they could not be).</summary>
    public RoofCommandHistoryPage? Page { get; private set; }

    /// <summary>Why nothing could be read: the controller unreachable or refusing, or the kiosk locked; null otherwise.</summary>
    public string? Problem { get; private set; }

    /// <summary>True while the person and their commands are read.</summary>
    public bool Reading { get; private set; }

    /// <summary>Said while the kiosk is locked (or the app signed out), or when it locked before the answer came.</summary>
    public string NeedsPerson => $"{_console.Wording.SignInAction} to see your roof commands.";

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>Forgets everything read: the kiosk was locked, or unlocked by someone.</summary>
    public void Reset()
    {
        // An answer still on its way was asked for by the person before: it is dropped, and never shown to the next.
        _reset.Cancel();
        _reset = new CancellationTokenSource();
        Person = null;
        Page = null;
        Problem = null;
        Reading = false;
        Raise();
    }

    /// <summary>
    /// Reads the person and their recent commands afresh, one read at a time. Nothing is shown if <see cref="Reset"/> came
    /// first.
    /// </summary>
    public async Task LoadAsync()
    {
        if (Reading)
        {
            return;
        }

        var reset = _reset.Token;
        Reading = true;
        Problem = null;
        Raise();
        RoofCallerResponse? person = null;
        RoofCommandHistoryPage? page = null;
        string? problem = null;
        try
        {
            person = await _console.ReadSignedInPersonAsync(reset);
            page = person is null ? null : await _console.ReadRecentHistoryAsync(Take, reset);
            if (person is null || page is null)
            {
                (person, page, problem) = (null, null, NeedsPerson);
            }
        }
        catch (Exception error)
        {
            // After a reset nothing is shown, so nothing is said either.
            problem = reset.IsCancellationRequested ? null
                : $"Your roof commands could not be read. {(error is NotSupportedException ? error.Message : RoofText.DescribeFailure(error))}";
        }

        if (reset.IsCancellationRequested)
        {
            return;
        }

        // When only the commands could not be read, the person's details still show.
        Person = person;
        Page = page;
        Problem = problem;
        Reading = false;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
