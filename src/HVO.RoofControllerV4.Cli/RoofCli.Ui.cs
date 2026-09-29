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

                var app = context.Host.CreateApplication();
                try
                {
                    using var ui = new RoofTerminalUi(context, app);
                    ui.Start();

                    // A termination signal closes the interface as F10 does: a motion held here is stopped first.
                    using var interrupted = cancellationToken.Register(() => ui.Post(ui.Quit));
                    context.Host.RunApplication(app, ui.Window);
                }
                finally
                {
                    app.Dispose();
                }

                return Task.FromResult((int)RoofExitCode.Success);
            });
            return command;
        }
    }
}
