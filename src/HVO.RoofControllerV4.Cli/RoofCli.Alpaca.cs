using System.CommandLine;
using System.Globalization;
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
                if (context.Json)
                {
                    context.WriteJson(alpaca);
                }
                else
                {
                    RoofCliFormat.WriteRows(context.Out, RoofCliAlpaca.DescribeRows(alpaca));
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
            var alert = new Option<string?>("--alert")
            {
                Description = "The refused Close's alert, from 'alpaca alerts': the grant lifts exactly its reasons."
            };
            var reasons = new Option<string[]>("--reason")
            {
                Description = "For scripts: a reason the grant lifts, as a refusal or alert gave it (scope-not-clear:<telescope> or scope-unknown:<telescope>). Repeat it for more.",
                AllowMultipleArgumentsPerToken = true
            };
            var yes = new Option<bool>("--yes", "-y")
            {
                Description = "Grant without asking: the question is still written on standard error."
            };
            var command = new Command(
                "grant",
                $"{RoofAlpacaText.GrantCloseLabel}: an override grant for one Alpaca Close despite a refused Close's reasons, for the "
                + "controller's override window (operator; a person's session). It never bypasses the fault latch, the interlocks, "
                + "the limits, the motor protections, a person's operator lease or the Stop hold.")
            {
                alert, reasons, yes
            };
            command.Validators.Add(result =>
            {
                var byAlert = result.GetResult(alert) is not null;
                var byReason = result.GetResult(reasons) is not null;
                if (byAlert == byReason)
                {
                    result.AddError("Give --alert ID (from 'alpaca alerts') or --reason KEY..., not both.");
                }
            });
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                // The reasons given are checked before anything is asked or sent.
                var given = parseResult.GetValue(reasons) is { Length: > 0 } keys ? RoofCliAlpaca.ParseReasons(keys) : null;
                using var client = ConnectPerson(context);
                var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                var alpaca = status.Alpaca ?? throw new RoofCliRefusedException(RoofCliAlpaca.NoState, RoofExitCode.Failed);

                RoofAlpacaAlert question;
                if (given is not null)
                {
                    question = new RoofAlpacaAlert { Kind = "Refused", Command = RoofAlpacaCommandKind.Close, ReasonKeys = given };
                }
                else
                {
                    var id = parseResult.GetRequiredValue(alert)!.Trim();
                    var alerts = await client.Alpaca.GetAlertsAsync(cancellationToken).ConfigureAwait(false);
                    question = alerts.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
                        ?? throw new RoofCliRefusedException(RoofCliAlpaca.NoSuchAlert(id));
                    if (!RoofAlpacaText.CanGrantFrom(question))
                    {
                        throw new RoofCliRefusedException(RoofCliAlpaca.NotGrantable(question));
                    }
                }

                // What the grant lifts, for whom and for how long: the person reads it before confirming.
                foreach (var line in RoofAlpacaText.DescribeGrantQuestion(question, alpaca.OverrideWindowMinutes))
                {
                    context.Host.Error.WriteLine(line);
                }

                if (!parseResult.GetValue(yes) && !Confirm(context, $"{RoofAlpacaText.GrantCloseLabel}?", "--yes"))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                // Exactly the reasons shown: the alert's own keys, or the ones given.
                var grant = await PersonOnlyAsync(() => client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, [.. question.ReasonKeys], cancellationToken))
                    .ConfigureAwait(false);
                return ReportDone(context, grant, $"Granted. {RoofAlpacaText.DescribeGrant(grant)}");
            });
            return command;
        }

        private Command CreateAlpacaRevokeCommand()
        {
            var kind = new Option<RoofAlpacaCommandKind>("--kind")
            {
                Description = "The grant's command: Close (the default; Alpaca Open is not available yet) or Open.",
                DefaultValueFactory = _ => RoofAlpacaCommandKind.Close,
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
                await PersonOnlyAsync(async () =>
                {
                    await client.Alpaca.RevokeAsync(which, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                return ReportDone(context, new { revoked = which.ToString() }, $"Revoked the {which} grant.");
            });
            return command;
        }

        private Command CreateAlpacaAlertsCommand()
        {
            var command = new Command(
                "alerts",
                "The Alpaca alerts, newest first: refused Alpaca commands, refusals as not authorised and warnings, with the client the Alpaca server reported.");
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

                RoofCliFormat.WriteTable(context.Out, RoofCliAlpaca.AlertHeaders, alerts.Select(RoofCliAlpaca.AlertRow));
                if (alerts.Any(RoofAlpacaText.CanGrantFrom))
                {
                    context.Out.WriteLine(RoofCliAlpaca.GrantHint);
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
        $"To allow one Close despite an alert's reasons: '{RoofCli.CommandName} alpaca grant --alert ID' (GRANT yes).";

    public static readonly string[] AlertHeaders = ["ID", "STATE", "GRANT", "ALERT"];

    /// <summary>Said with the grant question while the interface holds the operator lease: granting steps the person away.</summary>
    public const string StepAwayQuestion =
        "Granting steps you away from the operator lease this interface holds, as a grant never lifts a person's lease: until "
        + "you press a key, nothing here holds Alpaca commands off. Your next key takes control back and cancels the grant.";

    /// <summary>
    /// The banner after the person stepped away from the operator lease with <paramref name="grant"/> (null once it is used,
    /// lapsed or revoked): what Alpaca may now do, and that a key takes control back. <paramref name="lease"/> is the lease
    /// as the status shows it: another person's still refuses Alpaca commands.
    /// </summary>
    public static string DescribeSteppedAway(RoofOverrideGrant? grant, RoofOperatorLeaseState? lease)
    {
        if (lease is { Held: true, Holder: { Length: > 0 } holder })
        {
            return $"You stepped away from the operator lease, but {holder} holds it: Alpaca commands stay refused. "
                + (grant is null ? "Press any key to take control back." : "Press any key to take control back; that cancels the grant.");
        }

        return grant is null
            ? "You stepped away from the operator lease: Alpaca may move the roof. Press any key to take control back."
            : $"You stepped away from the operator lease: Alpaca may close the roof once in the next {Duration(grant.SecondsRemaining)}. "
                + "Press any key to take control back; that cancels the grant.";
    }

    /// <summary>Seconds as the shared wording gives them: whole minutes from 90 s, seconds below.</summary>
    public static string Duration(double seconds)
        => seconds >= 90
            ? $"{Math.Ceiling(seconds / 60).ToString(CultureInfo.InvariantCulture)} min"
            : $"{Math.Max(0, Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture)} s";

    /// <summary>Whose lease lapsed for want of input, while nobody holds one.</summary>
    public static string LeaseLapsed(string holder) => $"No operator lease: {holder}'s lapsed after no input, so Alpaca may move the roof.";

    public static string NoSuchAlert(string id) => $"There is no Alpaca alert '{id}'. '{RoofCli.CommandName} alpaca alerts' lists them.";

    public static string NotGrantable(RoofAlpacaAlert alert)
        => $"Alert {alert.Id} cannot be granted from: only a refused Close that is not acknowledged, whose every reason is a telescope "
            + "not clear of the roof or not known, can be. Nothing was sent.";

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

    public static IReadOnlyList<string> AlertRow(RoofAlpacaAlert alert) =>
    [
        alert.Id,
        DescribeAlertState(alert),
        RoofAlpacaText.CanGrantFrom(alert) ? "yes" : "no",
        RoofAlpacaText.DescribeAlert(alert)
    ];

    /// <summary>
    /// Reasons given with <c>grant --reason</c>: each <c>scope-not-clear:&lt;telescope&gt;</c> or
    /// <c>scope-unknown:&lt;telescope&gt;</c>, as a refusal or alert gave it. A usage error for anything else.
    /// </summary>
    public static IReadOnlyList<string> ParseReasons(IEnumerable<string> given)
    {
        var keys = new List<string>();
        foreach (var text in given)
        {
            var key = text.Trim();
            var colon = key.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == key.Length - 1
                || key[..colon] is not (RoofAlpacaContract.ScopeNotClearKind or RoofAlpacaContract.ScopeUnknownKind))
            {
                throw new RoofCliUsageException(
                    $"'{text}' is not a reason a Close grant can lift. Give {RoofAlpacaContract.ScopeNotClearKind}:<telescope> or "
                    + $"{RoofAlpacaContract.ScopeUnknownKind}:<telescope>, as the refusal or alert gave it.");
            }

            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
