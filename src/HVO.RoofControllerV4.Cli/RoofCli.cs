using System.CommandLine;
using System.CommandLine.Parsing;

namespace HVO.RoofControllerV4.Cli;

/// <summary>The <c>hvo-roof</c> process.</summary>
public static class RoofCliProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var termination = RoofCliTermination.Register();
        return await RoofCli.RunAsync(args, RoofCliHost.System(termination), termination.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>hvo-roof</c>: the roof controller's command-line client. Every command talks to the controller over its REST API
/// (and <c>status --watch</c> and <c>ui</c> over the status hub); nothing runs inside the controller.
/// </summary>
public static partial class RoofCli
{
    /// <summary>
    /// Runs one command line and returns its exit code (<see cref="RoofExitCode"/>). <paramref name="cancellationToken"/>
    /// is the interrupt (<see cref="RoofCliTermination.Token"/> in the real process): a command that has the roof moving
    /// sends Stop, and the command exits 130.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, RoofCliHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        var root = CreateRootCommand(host);
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                host.Error.WriteLine(error.Message);
            }

            host.Error.WriteLine($"Run '{CommandName} {DescribeCommand(parseResult)}--help' for usage.");
            return (int)RoofExitCode.Usage;
        }

        return await parseResult.InvokeAsync(
            new InvocationConfiguration
            {
                Output = host.Out,
                Error = host.Error,
                EnableDefaultExceptionHandler = false,

                // The signals are RoofCliTermination's: System.CommandLine's handler would end the process 5 s after
                // Ctrl+C, before a Stop is sure to be answered, and it does not handle SIGHUP at all.
                ProcessTerminationTimeout = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal const string CommandName = "hvo-roof";

    /// <summary>The global options, recognised after any command.</summary>
    internal sealed class GlobalOptions
    {
        public Option<bool> Json { get; } = new("--json")
        {
            Description = "Write results as JSON on standard output (errors too).",
            Recursive = true
        };

        public Option<Uri?> Controller { get; } = new("--controller")
        {
            Description = $"The controller's address, for example https://roof.local:5001/. Overrides {Client.RoofCredentialStore.ControllerVariable} and the credentials file.",
            Recursive = true,
            CustomParser = ParseControllerAddress
        };

        public Option<string?> CredentialsFile { get; } = new("--credentials-file")
        {
            Description = "The credentials file (default: $XDG_CONFIG_HOME/hvo-roof/credentials.json).",
            Recursive = true
        };

        public RoofCliContext CreateContext(RoofCliHost host, ParseResult parseResult)
            => new(host, parseResult.GetValue(Json), parseResult.GetValue(Controller), parseResult.GetValue(CredentialsFile));
    }

    internal static RootCommand CreateRootCommand(RoofCliHost host)
    {
        var options = new GlobalOptions();
        var root = new RootCommand(
            "The roof controller's command-line client. Stop: 'hvo-roof stop' (never needs a sign-in beyond the credential in use).")
        {
            options.Json,
            options.Controller,
            options.CredentialsFile
        };

        var builder = new CommandBuilder(host, options);
        foreach (var command in builder.CreateCommands())
        {
            root.Add(command);
        }

        return root;
    }

    /// <summary>Adds the command actions: each gets a context, and every failure becomes an exit code.</summary>
    internal sealed partial class CommandBuilder(RoofCliHost host, GlobalOptions options)
    {
        public IEnumerable<Command> CreateCommands()
        {
            yield return CreateStatusCommand();
            yield return CreateHealthCommand();
            yield return CreateStopCommand();
            yield return CreateOpenCommand();
            yield return CreateCloseCommand();
            yield return CreateLeaseCommand();
            yield return CreateClearFaultCommand();
            yield return CreateWhoAmICommand();
            yield return CreateLoginCommand();
            yield return CreateLogoutCommand();
            yield return CreatePasswordCommand();
            yield return CreateConfigCommand();
            yield return CreateUsersCommand();
            yield return CreatePinsCommand();
            yield return CreateKeysCommand();
            yield return CreateSessionsCommand();
            yield return CreateInfoCommand();
            yield return CreateRestartCommand();
            yield return CreateSetupCommand();
            yield return CreateUiCommand();
        }

        private void SetAction(Command command, Func<RoofCliContext, ParseResult, CancellationToken, Task<int>> action)
            => command.SetAction((parseResult, cancellationToken) => RunActionAsync(parseResult, action, cancellationToken));

        private async Task<int> RunActionAsync(
            ParseResult parseResult,
            Func<RoofCliContext, ParseResult, CancellationToken, Task<int>> action,
            CancellationToken cancellationToken)
        {
            RoofCliContext context;
            try
            {
                context = options.CreateContext(host, parseResult);
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                host.Error.WriteLine($"The credentials file path is not valid: {error.Message}");
                return (int)RoofExitCode.Usage;
            }

            try
            {
                return await action(context, parseResult, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Reported as any refusal is, so --json still writes its error document.
                return context.Fail(new RoofCliRefusedException("Interrupted.", RoofExitCode.Interrupted));
            }
            catch (Exception error)
            {
                return context.Fail(error);
            }
        }
    }

    private static Uri? ParseControllerAddress(ArgumentResult result)
    {
        var text = result.Tokens.Count == 0 ? string.Empty : result.Tokens[0].Value;
        if (TryParseControllerAddress(text, out var address, out var error))
        {
            return address;
        }

        result.AddError(error!);
        return null;
    }

    /// <summary>An absolute http or https address, with a trailing slash so relative routes resolve under it.</summary>
    internal static bool TryParseControllerAddress(string? text, out Uri? address, out string? error)
    {
        address = null;
        error = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host))
        {
            error = $"'{text}' is not a controller address. Give an http or https address, for example https://roof.local:5001/.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "The controller address may not include a user name, a query or a fragment.";
            return false;
        }

        address = parsed.AbsolutePath.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    private static string DescribeCommand(ParseResult parseResult)
    {
        var names = new List<string>();
        for (var result = parseResult.CommandResult; result.Parent is CommandResult parent; result = parent)
        {
            names.Insert(0, result.Command.Name);
        }

        return names.Count == 0 ? string.Empty : string.Join(' ', names) + " ";
    }
}
