using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        // ---- Alpaca (#220) --------------------------------------------------------------------------------------------

        private Command CreateAlpacaCommand()
        {
            var command = new Command(
                "alpaca",
                "The Alpaca command state: which commands are on, the Stop hold, the operator lease, override grants, alerts "
                + "and warnings. Clearing the hold, grants and acknowledging alerts need a person's session ('login'), never an API key.");
            command.Add(CreateAlpacaStatusCommand());
            command.Add(CreateAlpacaClearHoldCommand());
            command.Add(CreateAlpacaGrantCommand());
            command.Add(CreateAlpacaRevokeCommand());
            command.Add(CreateAlpacaAlertsCommand());
            command.Add(CreateAlpacaAcknowledgeCommand());
            return command;
        }

        private Command CreateAlpacaStatusCommand()
        {
            var command = new Command(
                "status",
                "Which Alpaca commands are on, the Stop hold, the operator lease, the override grants, the alerts (newest first) and the warnings.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                var alpaca = status.Alpaca ?? throw new RoofCliRefusedException(RoofCliAlpaca.NoState, RoofExitCode.Failed);
                AlpacaServerSummary? server = null;
                try
                {
                    server = (await client.System.GetSummaryAsync(cancellationToken).ConfigureAwait(false)).AlpacaServer;
                }
                catch (RoofApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Older controllers have the command state without deployment status.
                }
                if (context.Json)
                {
                    // Keep the existing command-state JSON fields, adding the separately deployed server's state.
                    var json = System.Text.Json.JsonSerializer.SerializeToNode(alpaca, RoofClientJson.Create())!.AsObject();
                    json["server"] = System.Text.Json.JsonSerializer.SerializeToNode(server, RoofClientJson.Create());
                    context.WriteJson(json);
                }
                else
                {
                    RoofCliFormat.WriteRows(context.Out, [.. RoofSystemText.DescribeAlpacaServer(server), .. RoofCliAlpaca.DescribeRows(alpaca)]);
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateAlpacaClearHoldCommand()
        {
            var command = new Command(
                "clear-hold",
                $"{RoofAlpacaText.ClearHoldLabel}: clear the Stop hold that every person's Stop sets, so Alpaca commands may run again "
                + "as the controller's settings allow (operator; a person's session).");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = ConnectPerson(context);
                var hold = await PersonOnlyAsync(() => client.Alpaca.ClearHoldAsync(cancellationToken)).ConfigureAwait(false);
                return ReportDone(context, hold, RoofCliAlpaca.HoldCleared);
            });
            return command;
        }

        private Command CreateAlpacaGrantCommand()
        {
            var alert = new Option<string>("--alert")
            {
                Description = "The refused Close's or Open's alert, from 'alpaca alerts': the grant is for one command of its kind and lifts exactly its reasons.",
                Required = true
            };
            var command = new Command(
                "grant",
                $"{RoofAlpacaText.GrantCloseLabel} or {RoofAlpacaText.GrantOpenLabel}: an override grant for one Alpaca Close (or Open) "
                + "despite a refused Close's (or Open's) reasons, for the controller's override window (operator; a person's session, "
                + "at a controlling foreground terminal: it asks for a fresh typed confirmation). It never bypasses the fault latch, the interlocks, the limits, "
                + "the motor protections, a person's operator lease or the Stop hold. No Open grant can be made while the safety "
                + "interlock is Block.")
            {
                alert
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = ConnectPerson(context);

                // Refuse common automation before any controller request; a pty alone does not prove a person.
                RequireGrantTerminal(context);

                var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                var alpaca = status.Alpaca ?? throw new RoofCliRefusedException(RoofCliAlpaca.NoState, RoofExitCode.Failed);
                var id = parseResult.GetRequiredValue(alert).Trim();
                var alerts = await client.Alpaca.GetAlertsAsync(cancellationToken).ConfigureAwait(false);
                var question = alerts.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
                    ?? throw new RoofCliRefusedException(RoofCliAlpaca.NoSuchAlert(id));
                if (!RoofAlpacaText.CanGrantFrom(question))
                {
                    throw new RoofCliRefusedException(RoofCliAlpaca.NotGrantable(question));
                }

                // The refused command's kind: a grant is for one Close or one Open. One the controller says cannot be made now
                // (an Open's while the safety interlock is Block) is not asked.
                var kind = question.Command ?? RoofAlpacaCommandKind.Close;
                if (RoofAlpacaText.GrantUnavailable(question, alpaca) is { } unavailable)
                {
                    throw new RoofCliRefusedException($"{RoofAlpacaText.DescribeGrantUnavailable(kind, unavailable)} Nothing was sent.");
                }

                // What the grant lifts, for whom and for how long: the person reads it before confirming.
                foreach (var line in RoofAlpacaText.DescribeGrantQuestion(question, alpaca.OverrideWindowMinutes))
                {
                    context.Host.Error.WriteLine(line);
                }

                var confirmation = $"{kind} {RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture)}";
                var answer = context.Host.ReadLine($"{RoofAlpacaText.GrantLabel(kind)}? Type '{confirmation}' to confirm: ", false);
                if (!string.Equals(answer?.Trim(), confirmation, StringComparison.Ordinal))
                {
                    context.Host.Error.WriteLine("Not confirmed; nothing was sent.");
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                RequireGrantTerminal(context);

                // Exactly the reasons shown: the alert's own keys.
                var grant = await PersonOnlyAsync(() => client.Alpaca.GrantAsync(kind, [.. question.ReasonKeys], cancellationToken))
                    .ConfigureAwait(false);
                return ReportDone(context, grant, $"Granted. {RoofAlpacaText.DescribeGrant(grant)}");
            });
            return command;
        }

        private Command CreateAlpacaRevokeCommand()
        {
            var kind = new Option<RoofAlpacaCommandKind?>("--kind")
            {
                Description = "The grant's command: Close or Open. Without it, revoke the only grant in force, or Close when both are in force.",
                CustomParser = result =>
                {
                    var text = result.Tokens.Count == 0 ? string.Empty : result.Tokens[0].Value;
                    if (Enum.TryParse<RoofAlpacaCommandKind>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                        && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        return parsed;
                    }

                    result.AddError($"'{text}' is not a command. Use Close or Open.");
                    return default;
                }
            };
            var command = new Command("revoke", $"{RoofAlpacaText.RevokeLabel} the override grant in force (operator; a person's session).") { kind };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var which = parseResult.GetValue(kind);
                using var client = ConnectPerson(context);
                if (which is null)
                {
                    var grants = await client.Alpaca.GetGrantsAsync(cancellationToken).ConfigureAwait(false);
                    which = grants.Count == 1 ? grants[0].Kind : RoofAlpacaCommandKind.Close;
                }
                await PersonOnlyAsync(async () =>
                {
                    await client.Alpaca.RevokeAsync(which.Value, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                return ReportDone(context, new { revoked = which.ToString() }, $"Revoked the {which} grant.");
            });
            return command;
        }

        private static void RequireGrantTerminal(RoofCliContext context)
        {
            if (!context.Host.IsInteractive || context.Json
                || RoofGrantTerminal.HasAutomationMarker(context.Host.GetEnvironmentVariable)
                || !context.Host.CheckGrantTerminal())
            {
                throw new RoofCliRefusedException(RoofCliAlpaca.GrantNeedsATerminal, RoofExitCode.ConfirmationRequired);
            }
        }

        private Command CreateAlpacaAlertsCommand()
        {
            var command = new Command(
                "alerts",
                "The Alpaca alerts, newest first: refused Alpaca commands, refusals as not authorised and warnings, with the client the "
                + "Alpaca server reported, and whether a grant can be made from each now.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var alerts = await client.Alpaca.GetAlertsAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(alerts);
                    return (int)RoofExitCode.Success;
                }

                if (alerts.Count == 0)
                {
                    context.Out.WriteLine(RoofCliAlpaca.NoAlerts);
                    return (int)RoofExitCode.Success;
                }

                // Whether a grant can be made now is the status's, as for grant: no Open grant in Block mode.
                var alpaca = (await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Alpaca;
                RoofCliFormat.WriteTable(context.Out, RoofCliAlpaca.AlertHeaders, alerts.Select(alert => RoofCliAlpaca.AlertRow(alert, alpaca)));
                if (alerts.Any(alert => RoofAlpacaText.CanGrantFrom(alert) && RoofAlpacaText.GrantUnavailable(alert, alpaca) is null))
                {
                    context.Out.WriteLine(RoofCliAlpaca.GrantHint);
                }

                foreach (var unavailable in alerts.Where(RoofAlpacaText.CanGrantFrom)
                    .Select(alert => RoofAlpacaText.GrantUnavailable(alert, alpaca) is { } reason ? RoofAlpacaText.DescribeGrantUnavailable(alert.Command ?? RoofAlpacaCommandKind.Close, reason) : null)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal))
                {
                    context.Out.WriteLine(unavailable);
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateAlpacaAcknowledgeCommand()
        {
            var id = new Argument<string>("id") { Description = "The alert's ID, from 'alpaca alerts'." };
            var command = new Command("ack", $"{RoofAlpacaText.AcknowledgeLabel} an Alpaca alert (a person's session).") { id };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var alertId = parseResult.GetRequiredValue(id).Trim();
                using var client = ConnectPerson(context);
                var alert = await PersonOnlyAsync(() => client.Alpaca.AcknowledgeAlertAsync(alertId, cancellationToken)).ConfigureAwait(false);
                return ReportDone(context, alert, $"Acknowledged alert {alert.Id}: {alert.Message}");
            });
            return command;
        }

        /// <summary>
        /// A client for one of the person-only Alpaca routes. An API key is refused here, before anything is sent: the
        /// controller refuses every API key there (<c>CredentialNotAllowed</c>).
        /// </summary>
        private static RoofControllerClient ConnectPerson(RoofCliContext context)
        {
            var connection = context.ResolveConnection();
            if (!context.Local && connection.Credential is RoofApiKeyCredential)
            {
                throw new RoofCliRefusedException(RoofCliAlpaca.PersonOnly, RoofExitCode.Forbidden);
            }

            return context.CreateClient(connection);
        }

        /// <summary>A person-only Alpaca request; the controller's refusal of the credential is said as the command line says it.</summary>
        private static async Task<T> PersonOnlyAsync<T>(Func<Task<T>> send)
        {
            try
            {
                return await send().ConfigureAwait(false);
            }
            catch (RoofApiException refusal) when (refusal.Code == RoofControllerErrorCode.CredentialNotAllowed)
            {
                throw new RoofCliRefusedException(RoofCliAlpaca.PersonOnly, RoofExitCode.Forbidden, refusal);
            }
        }
    }
}

/// <summary>
/// How <c>hvo-roof alpaca</c> and the terminal interface show the Alpaca command state (#220). The sentences are
/// <see cref="RoofAlpacaText"/>'s, shared with the other clients; this adds what only the terminal says.
/// </summary>
internal static class RoofCliAlpaca
{
    /// <summary>A person-only Alpaca action tried with an API key.</summary>
    public static readonly string PersonOnly =
        "Only a person's session may do this, and this credential is an API key: the controller refuses every API key for the "
        + $"Stop hold, override grants, the operator lease and alerts. Sign in with '{RoofCli.CommandName} login' first. Nothing was sent.";

    /// <summary>A status without the Alpaca command state: a controller older than it.</summary>
    public const string NoState = "The controller reports no Alpaca command state: it is older than this hvo-roof.";

    public const string HoldClear = "No Stop hold: Alpaca commands are not held after a Stop.";

    public const string NoLease = "No operator lease: no person's interface holds Alpaca commands off.";

    public const string NoGrants = "No override grant is in force.";

    public const string NoAlerts = "No Alpaca alerts.";

    public const string HoldCleared = "Stop hold cleared: Alpaca commands may run again, as the controller's settings allow.";

    public static readonly string GrantHint =
        $"To allow one Close or Open despite an alert's reasons: '{RoofCli.CommandName} alpaca grant --alert ID' (GRANT yes).";

    public static readonly string[] AlertHeaders = ["ID", "STATE", "GRANT", "ALERT"];

    /// <summary>Said with the grant question while the interface holds the operator lease: granting steps the person away.</summary>
    public const string StepAwayQuestion =
        "Granting steps you away from the operator lease this interface holds, as a grant never lifts a person's lease: until "
        + "you press a key, nothing here holds Alpaca commands off. Your next key takes control back and cancels the grant.";

    /// <summary>
    /// The banner after the person stepped away from the operator lease with a grant: what Alpaca may now do with the
    /// <paramref name="grants"/> in force (an Open grant, a Close grant, both, or none once they are used, lapsed or
    /// revoked), and that a key takes control back. <paramref name="lease"/> is the lease as the status shows it: another
    /// person's still refuses Alpaca commands.
    /// </summary>
    public static string DescribeSteppedAway(IReadOnlyList<RoofOverrideGrant> grants, RoofOperatorLeaseState? lease)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (lease is { Held: true, Holder: { Length: > 0 } holder })
        {
            return $"You stepped away from the operator lease, but {holder} holds it: Alpaca commands stay refused. "
                + grants.Count switch
                {
                    0 => $"{RoofAlpacaText.KeyToTakeBack} to take control back.",
                    1 => $"{RoofAlpacaText.KeyToTakeBack} to take control back; that cancels the grant.",
                    _ => $"{RoofAlpacaText.KeyToTakeBack} to take control back; that cancels the grants."
                };
        }

        return RoofAlpacaText.DescribeSteppedAway(grants, RoofAlpacaText.KeyToTakeBack);
    }

    /// <summary>Why a grant is refused without an eligible terminal (or with --json).</summary>
    public const string GrantNeedsATerminal =
        "A grant is a person's decision, asked and answered at a terminal: run 'hvo-roof alpaca grant --alert ID' "
        + "in the controlling foreground terminal, with stdin, stdout and stderr on that same terminal, without --json "
        + "or automation environment markers. Nothing was sent.";

    /// <summary>Whose lease lapsed for want of input, while nobody holds one.</summary>
    public static string LeaseLapsed(string holder) => $"No operator lease: {holder}'s lapsed after no input, so Alpaca may move the roof.";

    public static string NoSuchAlert(string id) => $"There is no Alpaca alert '{id}'. '{RoofCli.CommandName} alpaca alerts' lists them.";

    public static string NotGrantable(RoofAlpacaAlert alert)
        => $"Alert {alert.Id} cannot be granted from: only a refused Close or Open that is not acknowledged, whose every reason a grant "
            + "can lift, can be (for a Close, a telescope not clear of the roof or not known; for an Open, also a required weather "
            + "monitor's unsafe or unknown weather, no required weather source, or no telescope set to block the roof). Nothing was sent.";

    /// <summary>The Alpaca command state as label and value rows.</summary>
    public static IEnumerable<(string Label, string Value)> DescribeRows(RoofAlpacaStatus alpaca)
    {
        ArgumentNullException.ThrowIfNull(alpaca);
        yield return ("Commands", RoofAlpacaText.DescribeMode(alpaca.Mode));
        yield return ("Stop hold", RoofAlpacaText.DescribeHold(alpaca.Hold) ?? HoldClear);
        yield return ("Operator lease", DescribeLease(alpaca.OperatorLease));
        foreach (var warning in alpaca.Warnings)
        {
            yield return ("Warning", warning);
        }

        if (alpaca.Grants.Count == 0)
        {
            yield return ("Grants", NoGrants);
        }

        foreach (var grant in alpaca.Grants)
        {
            yield return ("Grant", RoofAlpacaText.DescribeGrant(grant));
        }

        if (alpaca.Alerts.Count == 0)
        {
            yield return ("Alerts", NoAlerts);
        }

        foreach (var alert in alpaca.Alerts)
        {
            yield return ("Alert", DescribeAlert(alert));
        }

        yield return ("Blocking telescopes", alpaca.BlockingScopes.ToString(CultureInfo.InvariantCulture));
        yield return ("Override window", $"{alpaca.OverrideWindowMinutes.ToString("0", CultureInfo.InvariantCulture)} min");
    }

    /// <summary>The operator lease: who holds it, whose lapsed for want of input, or that nobody does.</summary>
    public static string DescribeLease(RoofOperatorLeaseState? lease)
        => RoofAlpacaText.DescribeLease(lease)
            ?? (lease?.IdleLapsedHolder is { Length: > 0 } holder ? LeaseLapsed(holder) : NoLease);

    /// <summary>An alert on one line: its ID, whether it is new, and the shared sentence.</summary>
    public static string DescribeAlert(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return $"Alert {alert.Id} ({DescribeAlertState(alert)}): {RoofAlpacaText.DescribeAlert(alert)}";
    }

    public static string DescribeAlertState(RoofAlpacaAlert alert)
        => !alert.Acknowledged ? "NEW" : alert.AcknowledgedBy is { Length: > 0 } by ? $"acknowledged by {by}" : "acknowledged";

    /// <summary>
    /// An alert as a row of <c>alpaca alerts</c>: its ID, whether it is new, whether a grant can be made from it (<c>yes</c>,
    /// <c>no</c>, or <c>unavailable</c> for one <paramref name="alpaca"/> says cannot be made now: a refused Open's in Block
    /// mode), and the shared sentence.
    /// </summary>
    public static IReadOnlyList<string> AlertRow(RoofAlpacaAlert alert, RoofAlpacaStatus? alpaca) =>
    [
        alert.Id,
        DescribeAlertState(alert),
        !RoofAlpacaText.CanGrantFrom(alert) ? "no" : RoofAlpacaText.GrantUnavailable(alert, alpaca) is null ? "yes" : "unavailable",
        RoofAlpacaText.DescribeAlert(alert)
    ];
}
