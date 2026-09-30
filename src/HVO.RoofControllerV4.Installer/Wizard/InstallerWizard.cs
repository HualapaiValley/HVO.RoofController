using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Installer.Wizard;

/// <summary>
/// The installer's wizard, in HVO Dark: what the machine is, the roles to install, their questions, the plan to review
/// (and save as answers), the install, and what was done. A person types nothing but a confirmation here: no secret is
/// asked for, shown or saved.
/// </summary>
internal sealed class InstallerWizard : IDisposable
{
    /// <summary>The keys of the key bar, and what each does (Enter does what the Next button says).</summary>
    internal static readonly (string Key, string Name)[] Keys = [("Enter", "Next"), ("Esc", "Back"), ("F10", "Quit")];

    internal const string WaitForInstallText = "Installing: the installer closes when the install has finished or stopped.";

    private readonly IApplication _app;
    private readonly Label _header;
    private readonly Label _message;
    private readonly FrameView _content;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Label _enterName;
    private readonly Label[] _enterKey;
    private readonly Label[] _escKey;
    private readonly List<WizardPage> _pages;
    private int _index = -1;
    private int _pending;
    private bool _closed;

    public InstallerWizard(IApplication app, InstallerSession session, Func<string, string?> environment)
    {
        _app = app;
        Session = session;
        Theme = RoofUiTheme.For(environment);

        Window = new Window { Title = WindowTitle(session.Version), X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Window.SetScheme(Theme.Base);
        Theme.SetFrame(Window, Theme.WindowFrame);

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill() };
        _header.SetScheme(Theme.Header);
        _back = Theme.Styled(new Button { Text = "Back", X = 0, Y = Pos.AnchorEnd(2) });
        _back.Accepting += (_, e) =>
        {
            e.Handled = true;
            Back();
        };
        _next = Theme.Styled(new Button { Text = "Next", X = Pos.Right(_back) + 2, Y = Pos.AnchorEnd(2), IsDefault = true });
        _next.Accepting += (_, e) =>
        {
            e.Handled = true;
            Next();
        };
        _message = new Label { X = Pos.Right(_next) + 2, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        _content = new FrameView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
        Theme.SetFrame(_content);
        var keyBar = Theme.CreateKeyBar(Keys);
        var keyLabels = keyBar.SubViews.OfType<Label>().ToArray();
        _enterName = keyLabels[1];
        _enterKey = keyLabels[0..2];
        _escKey = keyLabels[2..4];
        Window.Add(_header, _content, _back, _next, _message, keyBar);

        _pages =
        [
            new MachinePage(this),
            new RolesPage(this),
            new SettingsPage(this),
            new ReviewPage(this),
            new InstallingPage(this),
            new DonePage(this)
        ];
        _app.Keyboard.KeyDown += OnKeyDown;
        ShowPage(0);
    }

    public Window Window { get; }

    /// <summary>The window's title: the release it installs (its commit is in the log and --version).</summary>
    public static string WindowTitle(string version) => $"HVO Roof installer {version.Split('+')[0]}";

    public RoofUiTheme Theme { get; }

    public InstallerSession Session { get; }

    /// <summary>How the installer ends: cancelled until an install succeeds (or fails).</summary>
    public InstallerExitCode Result { get; set; } = InstallerExitCode.Cancelled;

    /// <summary>Why the install failed, when it did: written to the terminal after the wizard closes.</summary>
    public string? Failure { get; set; }

    /// <summary>True while the install runs: the wizard does not close meanwhile.</summary>
    public bool Installing { get; set; }

    /// <summary>Work still on its way (tests wait for none).</summary>
    public int PendingOperations => Volatile.Read(ref _pending);

    public WizardPage Page => _pages[_index];

    public int PageIndex => _index;

    public IReadOnlyList<WizardPage> Pages => _pages;

    public string Message => _message.Text;

    public string Header => _header.Text;

    public Button NextButton => _next;

    public Button BackButton => _back;

    /// <summary>What the key bar says Enter does: what the Next button does.</summary>
    public string EnterName => _enterName.Text;

    /// <summary>Shows page <paramref name="index"/>, which gets ready for what the earlier pages chose.</summary>
    public void ShowPage(int index)
    {
        if (_index >= 0)
        {
            _content.Remove(Page);
        }

        _index = index;
        _header.Text = $"Step {index + 1} of {_pages.Count}: {Page.PageTitle}";
        _content.Title = Page.PageTitle;
        Say(string.Empty);
        _content.Add(Page);
        Page.Showing();
        Refresh();
        Page.SetFocus();
    }

    /// <summary>Brings the buttons up to date with the page: what Next does, and whether Back and Next may be used.</summary>
    public void Refresh()
    {
        _next.Text = Page.NextLabel;
        _enterName.Text = Page.NextLabel;
        _next.Enabled = Page.CanGoNext;
        _back.Enabled = _index > 0 && Page.CanGoBack;

        // The key bar dims a key that does nothing here.
        foreach (var label in _enterKey)
        {
            label.Enabled = _next.Enabled;
        }

        foreach (var label in _escKey)
        {
            label.Enabled = _back.Enabled;
        }
    }

    public void Next()
    {
        if (!Page.CanGoNext)
        {
            return;
        }

        var problem = Page.Leave();
        if (problem is not null)
        {
            Say(problem, error: true);
            return;
        }

        if (_index < _pages.Count - 1)
        {
            ShowPage(_index + 1);
        }
    }

    public void Back()
    {
        if (_index > 0 && Page.CanGoBack)
        {
            ShowPage(_index - 1);
        }
    }

    /// <summary>Closes the wizard, unless the install is running: that finishes (or stops) first.</summary>
    public void Quit()
    {
        if (Installing)
        {
            Say(WaitForInstallText, error: true);
            return;
        }

        _app.RequestStop();
    }

    public void Say(string text, bool error = false)
    {
        _message.Text = text;
        _message.SetScheme(error ? Theme.Danger : Theme.Base);
    }

    /// <summary>
    /// Runs <paramref name="work"/> away from the interface's thread, then <paramref name="done"/> on it with the result
    /// or the error.
    /// </summary>
    public void Run<T>(Func<Task<T>> work, Action<T?, Exception?> done)
    {
        Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
        {
            T? result = default;
            Exception? failure = null;
            try
            {
                result = await work().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
            }

            if (!Post(() =>
                {
                    try
                    {
                        done(result, failure);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }))
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the interface's thread; false when the wizard has closed.</summary>
    public bool Post(Action action)
    {
        if (_closed)
        {
            return false;
        }

        try
        {
            _app.Invoke(() =>
            {
                if (!_closed)
                {
                    action();
                }
            });
            return true;
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.Esc)
        {
            // Esc goes back a page; it never closes the installer.
            key.Handled = true;
            Back();
            return;
        }

        if (key == Key.F10)
        {
            key.Handled = true;
            Quit();
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _app.Keyboard.KeyDown -= OnKeyDown;
        _content.Remove(Page);
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        Window.Dispose();
    }
}
