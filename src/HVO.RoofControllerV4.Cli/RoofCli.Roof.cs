using System.CommandLine;
using System.Threading.Channels;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateStatusCommand()
        {
            var watch = new Option<bool>("--watch", "-w")
            {
                Description = "Follow the status hub: one line per change, until Ctrl+C. Says so when the status goes stale."
            };
            var command = new Command("status", "Show the roof's status.") { watch };
            SetAction(command, (context, parseResult, cancellationToken) => parseResult.GetValue(watch)
                ? WatchStatusAsync(context, cancellationToken)
                : ShowStatusAsync(context, cancellationToken));
            return command;
        }

        private static async Task<int> ShowStatusAsync(RoofCliContext context, CancellationToken cancellationToken)
        {
            using var client = context.Connect();
            var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                context.WriteJson(status);
            }
            else
            {
                RoofCliFormat.WriteRows(context.Out, RoofCliFormat.DescribeStatus(status));
            }

            return (int)RoofExitCode.Success;
        }

        private static async Task<int> WatchStatusAsync(RoofCliContext context, CancellationToken cancellationToken)
        {
            using var client = context.Connect();
            await using var feed = client.CreateStatusFeed();
            var gate = new object();
            var wasStale = false;

            // Set once the watch has ended: disposing the feed disconnects it, which is not news to report.
            var ended = false;
            var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            feed.StatusReceived += (_, e) =>
            {
                lock (gate)
                {
                    if (ended)
                    {
                        return;
                    }

                    wasStale = false;
                    if (context.Json)
                    {
                        context.WriteJsonLine(new { stale = false, e.Message.Sequence, e.Message.InstanceId, restarted = e.IsNewInstance, e.Status });
                        return;
                    }

                    if (e.IsNewInstance && e.Previous is not null)
                    {
                        context.Out.WriteLine("The controller restarted.");
                    }

                    if (e.SafetyAlert is { } alert)
                    {
                        context.Out.WriteLine($"SAFETY: {alert.Title}. {alert.Message}");
                    }

                    context.Out.WriteLine(RoofCliFormat.DescribeLine(e.Status));
                }
            };
            feed.StateChanged += (_, _) =>
            {
                lock (gate)
                {
                    if (feed.State == RoofStatusFeedState.Unauthorized)
                    {
                        refused.TrySetResult();
                        return;
                    }

                    if (ended || feed.StaleSince is not { } since || wasStale)
                    {
                        return;
                    }

                    wasStale = true;
                    if (context.Json)
                    {
                        context.WriteJsonLine(new { stale = true, staleSince = since });
                    }
                    else
                    {
                        context.Out.WriteLine(DescribeStale(feed.Status, since));
                    }
                }
            };

            feed.Start();
            try
            {
                await refused.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw feed.LastError as RoofApiException
                    ?? new RoofApiException(System.Net.HttpStatusCode.Unauthorized);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    ended = true;
                    return (int)(feed.IsStale ? RoofExitCode.Stale : RoofExitCode.Success);
                }
            }
        }

        internal static string DescribeStale(RoofStatusResponse? last, DateTimeOffset since)
        {
            var known = last is null ? "No status has been received." : $"Last known: {RoofCliFormat.DescribeRoof(last)}.";
            return $"STALE: no status from the controller since {RoofCliFormat.Time(since)}. {known} "
                + $"Stop still works: '{CommandName} stop'.";
        }

        private Command CreateHealthCommand()
        {
            var probe = new Option<string?>("--probe")
            {
                Description = "Ask the anonymous probe instead of the full report: 'ready' or 'live'. Needs no credential."
            };
            probe.AcceptOnlyFromAmong("ready", "live");
            var command = new Command("health", "Show the controller's health checks. Exits 8 unless it is healthy.") { probe };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                if (parseResult.GetValue(probe) is { } which)
                {
                    var result = which == "ready"
                        ? await client.Health.GetReadinessAsync(cancellationToken).ConfigureAwait(false)
                        : await client.Health.GetLivenessAsync(cancellationToken).ConfigureAwait(false);
                    if (context.Json)
                    {
                        context.WriteJson(new { probe = which, statusCode = (int)result.StatusCode, result.Status, result.IsHealthy });
                    }
                    else
                    {
                        context.Out.WriteLine($"{which}: {result.Status}");
                    }

                    return (int)(result.IsHealthy ? RoofExitCode.Success : RoofExitCode.Unhealthy);
                }

                var report = await client.Health.GetReportAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(report);
                }
                else
                {
                    context.Out.WriteLine($"Health: {report.Status}");
                    RoofCliFormat.WriteTable(
                        context.Out,
                        ["CHECK", "STATUS", "DESCRIPTION"],
                        report.Checks.Select(check => (IReadOnlyList<string>)[check.Name, check.Status, check.Description ?? string.Empty]));
                }

                return (int)(string.Equals(report.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                    ? RoofExitCode.Success
                    : RoofExitCode.Unhealthy);
            });
            return command;
        }

        private Command CreateStopCommand()
        {
            var command = new Command("stop", $"{RoofStopText.ButtonLabel}. Sent at once over REST; exits 9 when the relays could not be verified.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                return ReportStop(context, await client.StopAsync(cancellationToken).ConfigureAwait(false));
            });
            return command;
        }

        /// <summary>Writes a Stop result and returns its exit code. The message is the shared Stop wording.</summary>
        internal static int ReportStop(RoofCliContext context, RoofStopResult result)
        {
            var code = result.Outcome switch
            {
                RoofStopOutcome.Acknowledged => RoofExitCode.Success,
                RoofStopOutcome.RelayUnverified => RoofExitCode.StopNotVerified,

                // Not delivered, or no answer: nothing confirms the stop, so the operator must go to the roof.
                _ when result.Error is HttpRequestException or TimeoutException or OperationCanceledException => RoofExitCode.StopNotVerified,
                _ when result.Error is { } error => RoofCliContext.Classify(error).Code,
                _ => RoofExitCode.Failed
            };

            if (context.Json)
            {
                var refusal = result.Error as RoofApiException;
                context.WriteJson(new
                {
                    outcome = result.Outcome.ToString(),
                    result.Message,
                    exitCode = (int)code,
                    code = refusal?.CodeText,
                    result.Status
                });
            }
            else
            {
                (code == RoofExitCode.Success ? context.Out : context.Host.Error).WriteLine(result.Message);
            }

            return (int)code;
        }

        private Command CreateOpenCommand() => CreateMotionCommand("open", "Open the roof.", RoofMotionDirection.Opening);

        private Command CreateCloseCommand() => CreateMotionCommand("close", "Close the roof.", RoofMotionDirection.Closing);

        private Command CreateMotionCommand(string name, string description, RoofMotionDirection direction)
        {
            var noWait = new Option<bool>("--no-wait")
            {
                Description = "Return once the controller accepts the command. Without it, the command follows the motion, renews the operator lease, and sends Stop on Ctrl+C."
            };
            var command = new Command(name, description + " Follows the motion until it ends; Ctrl+C sends Stop.") { noWait };
            SetAction(command, (context, parseResult, cancellationToken) => MoveAsync(context, direction, !parseResult.GetValue(noWait), cancellationToken));
            return command;
        }

        private static async Task<int> MoveAsync(RoofCliContext context, RoofMotionDirection direction, bool follow, CancellationToken cancellationToken)
        {
            using var client = context.Connect();
            var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";
            RoofStatusResponse status;
            try
            {
                status = direction == RoofMotionDirection.Opening
                    ? await client.Roof.OpenAsync(cancellationToken).ConfigureAwait(false)
                    : await client.Roof.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The command may have reached the controller before Ctrl+C, and the roof may be moving.
                return await StopOnInterruptAsync(context, client).ConfigureAwait(false);
            }

            if (!follow || !status.IsMoving)
            {
                if (context.Json)
                {
                    context.WriteJson(status);
                }
                else
                {
                    context.Out.WriteLine($"{verb} accepted. Roof: {RoofCliFormat.DescribeRoof(status)}.");
                    if (status.IsMoving && status.LeaseSecondsRemaining is { } lease)
                    {
                        context.Out.WriteLine(
                            $"The controller holds this motion on an operator lease ({RoofCliFormat.Seconds(lease)}): it stops the roof unless '{CommandName} lease' renews it.");
                    }
                }

                return (int)RoofExitCode.Success;
            }

            if (!context.Json)
            {
                context.Out.WriteLine($"{verb} accepted. Following the motion; Ctrl+C sends Stop.");
                context.Out.WriteLine(RoofCliFormat.DescribeLine(status));
            }

            return await FollowMotionAsync(context, client, direction, status, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Ctrl+C during a motion command: sends Stop, reports it, and exits as interrupted.</summary>
        private static async Task<int> StopOnInterruptAsync(RoofCliContext context, RoofControllerClient client)
        {
            var result = await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            context.Host.Error.WriteLine("Interrupted: Stop sent.");
            ReportStop(context, result);
            return (int)RoofExitCode.Interrupted;
        }

        /// <summary>
        /// Follows a motion on the status hub until it ends, renewing the operator lease over REST when one applies. Ctrl+C
        /// sends Stop. A lease that cannot be renewed is reported: the controller stops the roof when it runs out.
        /// </summary>
        private static async Task<int> FollowMotionAsync(
            RoofCliContext context,
            RoofControllerClient client,
            RoofMotionDirection direction,
            RoofStatusResponse accepted,
            CancellationToken cancellationToken)
        {
            var updates = Channel.CreateUnbounded<RoofStatusResponse>(new UnboundedChannelOptions { SingleReader = true });
            await using var feed = client.CreateStatusFeed();
            feed.StatusReceived += (_, e) => updates.Writer.TryWrite(e.Status);
            feed.Start();

            var time = context.Host.Time;
            var status = accepted;
            var renewAt = NextRenewal(status);
            var lastLine = RoofCliFormat.DescribeRoof(status);
            try
            {
                while (true)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var delay = renewAt is { } due ? Max(due - time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
                    var timer = Task.Delay(delay, time, wait.Token);
                    var next = updates.Reader.WaitToReadAsync(wait.Token).AsTask();
                    var finished = await Task.WhenAny(timer, next).ConfigureAwait(false);
                    await wait.CancelAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (finished == next)
                    {
                        while (updates.Reader.TryRead(out var update))
                        {
                            if (RoofStatusRules.ShouldApply(status, update))
                            {
                                status = update;
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            status = await client.Roof.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (RoofApiException refusal) when (refusal.Code == RoofControllerErrorCode.LeaseNotActive)
                        {
                            status = refusal.RoofStatus ?? await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is HttpRequestException or TimeoutException
                            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                        {
                            context.Host.Error.WriteLine(
                                $"The lease could not be renewed: {RoofText.DescribeFailure(error)} If the controller is running, it stops the roof when the lease runs out.");
                            return (int)RoofExitCode.Unreachable;
                        }
                    }

                    renewAt = status.IsMoving ? NextRenewal(status) : null;
                    var line = RoofCliFormat.DescribeRoof(status);
                    if (!context.Json && line != lastLine)
                    {
                        context.Out.WriteLine(RoofCliFormat.DescribeLine(status));
                        lastLine = line;
                    }

                    if (!status.IsMoving)
                    {
                        return ReportMotionEnd(context, direction, status);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await StopOnInterruptAsync(context, client).ConfigureAwait(false);
            }

            DateTimeOffset? NextRenewal(RoofStatusResponse current)
                => RoofStatusRules.GetLeaseRenewalDelay(current.LeaseSecondsRemaining) is { } after ? time.GetUtcNow() + after : null;
        }

        private static int ReportMotionEnd(RoofCliContext context, RoofMotionDirection direction, RoofStatusResponse status)
        {
            var reached = direction == RoofMotionDirection.Opening
                ? status.Status == RoofControllerStatus.Open
                : status.Status == RoofControllerStatus.Closed;
            if (context.Json)
            {
                context.WriteJson(status);
            }
            else if (reached)
            {
                context.Out.WriteLine($"Done. Roof: {RoofCliFormat.DescribeRoof(status)}.");
            }
            else
            {
                context.Host.Error.WriteLine(
                    $"The roof stopped before the end: {RoofCliFormat.DescribeRoof(status)}. {RoofCliFormat.DescribeLastStop(status)}.");
            }

            return (int)(reached ? RoofExitCode.Success : RoofExitCode.Failed);
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

        private Command CreateLeaseCommand()
        {
            var command = new Command("lease", "Renew the operator lease of the motion in progress.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var status = await client.Roof.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(status);
                }
                else
                {
                    context.Out.WriteLine(status.LeaseSecondsRemaining is { } lease
                        ? $"Lease renewed: {RoofCliFormat.Seconds(lease)} left. Roof: {RoofCliFormat.DescribeRoof(status)}."
                        : $"Lease renewed. Roof: {RoofCliFormat.DescribeRoof(status)}.");
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateClearFaultCommand()
        {
            var pulse = new Option<int?>("--pulse-ms")
            {
                Description = "Length of the drive's fault-reset pulse in milliseconds (the controller's default when left out)."
            };
            pulse.Validators.Add(result =>
            {
                if (result.GetValueOrDefault<int?>() is < 1)
                {
                    result.AddError("--pulse-ms must be a positive number of milliseconds.");
                }
            });
            var command = new Command("clear-fault", "Clear a latched fault once its cause is resolved.") { pulse };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var status = await client.Roof.ClearFaultAsync(parseResult.GetValue(pulse), cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(status);
                }
                else
                {
                    context.Out.WriteLine($"Clear fault accepted. Fault: {RoofCliFormat.DescribeFault(status)}. Roof: {RoofCliFormat.DescribeRoof(status)}.");
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateWhoAmICommand()
        {
            var command = new Command("whoami", "Show the controller in use and who the controller says the credential is.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                var connection = context.ResolveConnection();
                using var client = context.CreateClient(connection);
                var caller = await client.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(new { controller = connection.Controller, credentialSource = connection.Source, caller });
                }
                else
                {
                    RoofCliFormat.WriteRows(context.Out, DescribeCaller(connection, caller));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        internal static IEnumerable<(string, string)> DescribeCaller(RoofCliConnection connection, RoofCallerResponse caller)
        {
            yield return ("Controller", connection.Controller.ToString());
            yield return ("Credential", connection.Source);
            yield return ("Name", caller.Name);
            yield return ("Role", RoofCliFormat.Role(caller.Role));
            var kind = caller.Kind switch
            {
                RoofCredentialKind.ApiKey => "API key",
                RoofCredentialKind.Session => "session (password)",
                RoofCredentialKind.Pin => "session (PIN)",
                _ => caller.Kind.ToString()
            };
            yield return ("Kind", caller.IsKiosk ? $"{kind}, kiosk" : kind);
            if (caller.ExpiresUtc is { } expires)
            {
                yield return ("Expires", RoofCliFormat.Time(expires));
            }
        }
    }
}
