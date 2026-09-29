using System.CommandLine;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateUiCommand()
        {
            var command = new Command(
                "ui",
                $"Open the terminal interface: the roof, settings, people, the controller and setup, with {RoofStopText.ButtonLabel} (F9) on every page.");

            // Not async: Terminal.Gui runs on the thread that created it.
            SetAction(command, (context, _, cancellationToken) =>
            {
                if (context.Json)
                {
                    throw new RoofCliUsageException("The terminal interface has no JSON output; leave out --json.");
                }

                if (!context.Host.IsInteractive)
                {
                    throw new RoofCliUsageException("The terminal interface needs a terminal, and the input is redirected.");
                }

                // While the interface is open, no signal ends the process at once: a termination signal closes it as F10
                // does, a motion started here is stopped first, and a Stop on its way is answered. The process waits for
                // that and for the terminal to be restored (up to RoofCliTermination.StopGrace). The terminal closing
                // sends SIGHUP twice, and the second must not end the process before the Stop that the first asks for.
                using var hold = context.Host.Termination?.Hold();
                var app = context.Host.CreateApplication();
                RoofStopResult? unconfirmed;
                try
                {
                    using var ui = new RoofTerminalUi(context, app);
                    ui.Start();
                    using var interrupted = cancellationToken.Register(() => ui.Post(() => ui.Quit(interrupted: true)));
                    context.Host.RunApplication(app, ui.Window);
                    unconfirmed = ui.UnconfirmedStop;
                }
                finally
                {
                    app.Dispose();
                }

                // The interface is gone, and with it the Stop result it showed: a Stop that nothing confirmed is said
                // again on the restored terminal.
                if (unconfirmed is not null)
                {
                    context.Host.Error.WriteLine($"The last Stop sent from the interface was not confirmed. {unconfirmed.Message}");
                }

                return Task.FromResult((int)(cancellationToken.IsCancellationRequested ? RoofExitCode.Interrupted
                    : unconfirmed is not null ? RoofExitCode.StopNotVerified
                    : RoofExitCode.Success));
            });
            return command;
        }
    }
}
