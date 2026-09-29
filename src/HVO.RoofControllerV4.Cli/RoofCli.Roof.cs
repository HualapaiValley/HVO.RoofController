using System.Collections.Concurrent;
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

        /// <summary>
        /// A status as <c>status --watch --json</c> compares it: every field but the times that advance with each
        /// snapshot and each read of the HAT.
        /// </summary>
        internal static string DescribeJsonState(RoofStatusResponse status) => System.Text.Json.JsonSerializer.Serialize(
            status with { StatusVersion = 0, SnapshotUtc = default, LastSuccessfulRelayReadUtc = null, LastSuccessfulInputReadUtc = null },
            RoofCliJson.Compact);

        private static async Task<int> WatchStatusAsync(RoofCliContext context, CancellationToken cancellationToken)
        {
            using var client = context.Connect();
            await using var feed = client.CreateStatusFeed();
            var gate = new object();
            var started = context.Host.Time.GetUtcNow();
            var received = false;
            var saidStale = false;

            // The last state written, without its times: the hub repeats an unchanged status every second, and the
            // watch writes one line per change. A line is the text summary; a JSON line is the whole status, so a
            // change to any of its fields (the inputs' health, say) is written.
            string? lastState = null;

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

                    received = true;
                    var restarted = e.IsNewInstance && e.Previous is not null;
                    var state = context.Json ? DescribeJsonState(e.Status) : RoofCliFormat.DescribeState(e.Status);
                    if (state == lastState && !saidStale && !restarted && e.SafetyAlert is null)
                    {
                        return;
                    }

                    lastState = state;
                    saidStale = false;
                    if (context.Json)
                    {
                        context.WriteJsonLine(new { stale = false, e.Message.Sequence, e.Message.InstanceId, restarted = e.IsNewInstance, e.Status });
                        return;
                    }

                    if (restarted)
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

                    if (!ended && !saidStale && feed.StaleSince is { } since)
                    {
                        SayStale(feed.Status, since);
                    }
                }
            };

            // The feed goes stale only once it has had a status; a watch that never gets one says so too.
            using var nothingYet = context.Host.Time.CreateTimer(
                _ =>
                {
                    lock (gate)
                    {
                        if (!ended && !received && !saidStale)
                        {
                            SayStale(null, started);
                        }
                    }
                },
                null,
                context.StatusFeed.StaleAfter,
                Timeout.InfiniteTimeSpan);

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
                    return (int)(received && !feed.IsStale ? RoofExitCode.Success : RoofExitCode.Stale);
                }
            }

            void SayStale(RoofStatusResponse? last, DateTimeOffset since)
            {
                saidStale = true;
                if (context.Json)
                {
                    context.WriteJsonLine(new { stale = true, staleSince = since });
                }
                else
                {
                    context.Out.WriteLine(DescribeStale(last, since));
                }
            }
        }

        internal static string DescribeStale(RoofStatusResponse? last, DateTimeOffset since)
        {
            var stale = last is null
                ? $"STALE: no status has been received (waiting since {RoofCliFormat.Time(since)})."
                : $"STALE: no status from the controller since {RoofCliFormat.Time(since)}. Last known: {RoofCliFormat.DescribeRoof(last)}.";
            return $"{stale} Stop still works: '{CommandName} stop'.";
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
            SetAction(command, async (context, _, _) =>
            {
                // A signal does not cut the Stop short: it is sent and answered, and the process waits for it.
                using var hold = context.Host.Termination?.Hold();
                using var client = context.Connect();
                return ReportStop(context, await client.StopAsync(CancellationToken.None).ConfigureAwait(false));
            });
            return command;
        }

        /// <summary>Writes a Stop result and returns its exit code. The message is the shared Stop wording.</summary>
        internal static int ReportStop(RoofCliContext context, RoofStopResult result)
        {
            var code = StopExitCode(result);
            if (context.Json)
            {
                context.WriteJson(DescribeStop(result, code));
            }
            else
            {
                (code == RoofExitCode.Success ? context.Out : context.Host.Error).WriteLine(result.Message);
            }

            return (int)code;
        }

        /// <summary>
        /// 0 when the controller acknowledged the Stop; 5, 6 or 7 when it refused it (a 4xx answer: signed out, not allowed,
        /// or refused); otherwise 9, because nothing confirms that the roof stopped: the relays could not be verified, the
        /// Stop was not delivered or not answered, or the answer was a server error (a 503 included: the controller could not
        /// verify the stop, or its hardware is unavailable) or could not be read.
        /// </summary>
        internal static RoofExitCode StopExitCode(RoofStopResult result) => result.Outcome switch
        {
            RoofStopOutcome.Acknowledged => RoofExitCode.Success,
            RoofStopOutcome.RelayUnverified => RoofExitCode.StopNotVerified,
            _ when result.Error is RoofApiException { StatusCode: < System.Net.HttpStatusCode.InternalServerError } refusal
                => RoofCliContext.Classify(refusal).Code,
            _ => RoofExitCode.StopNotVerified
        };

        /// <summary>A Stop result as <c>stop --json</c> writes it, whatever the outcome (docs/cli.md).</summary>
        private static object DescribeStop(RoofStopResult result, RoofExitCode code) => new
        {
            outcome = result.Outcome.ToString(),
            result.Message,
            exitCode = (int)code,
            code = (result.Error as RoofApiException)?.CodeText,
            result.Status
        };

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
            // From before the command is sent until this command ends, the roof may move on its order: no signal ends
            // the process before the Stop that the first one asks for. The terminal closing sends SIGHUP twice, well
            // before this command has seen the first.
            using var hold = context.Host.Termination?.Hold();
            using var client = context.Connect();
            var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";

            // Ctrl+C does not cancel the command at once: the controller may already have it, and a Stop sent sooner
            // could reach it first. Stop is sent once the command is answered, or after MotionAnswerWait without an
            // answer, when the command is cancelled; it may then still reach the controller after the Stop.
            using var answerWait = new CancellationTokenSource(Timeout.InfiniteTimeSpan, context.Host.Time);
            using var interrupted = cancellationToken.Register(() => answerWait.CancelAfter(MotionAnswerWait));
            RoofStatusResponse status;
            try
            {
                status = direction == RoofMotionDirection.Opening
                    ? await client.Roof.OpenAsync(answerWait.Token).ConfigureAwait(false)
                    : await client.Roof.CloseAsync(answerWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (answerWait.IsCancellationRequested)
            {
                return await StopOnInterruptAsync(context, client, unanswered: verb).ConfigureAwait(false);
            }
            catch (Exception error) when (cancellationToken.IsCancellationRequested)
            {
                // Ended after Ctrl+C, whatever the outcome: Ctrl+C sends Stop, now after the command. When the command got
                // no answer (the connection ended, say), it may still reach the controller after the Stop.
                return await StopOnInterruptAsync(context, client, unanswered: MayHaveReachedController(error) ? verb : null)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (MayHaveReachedController(error))
            {
                // Not "not sent": the controller may have acted on it. The exit code stays the failure's.
                throw new RoofCliRefusedException(
                    Unanswered(error, verb, $"run '{CommandName} stop', or use the stop control at the roof"), RoofCliContext.Classify(error).Code, error);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Accepted after Ctrl+C: the roof may be moving on it.
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

        /// <summary>
        /// True when Open or Close failed with no answer from the controller after it may have been sent: a timeout, the
        /// connection dropping, an answer that could not be read, or a proxy's 502 or 504 (the controller answers Open and
        /// Close with neither). The controller may have acted on it. A connection that could not be made sent nothing.
        /// </summary>
        internal static bool MayHaveReachedController(Exception error) => error switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } => false,
            HttpRequestException or TimeoutException or TaskCanceledException { InnerException: TimeoutException } or RoofProtocolException => true,
            RoofApiException { StatusCode: System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout } => true,
            _ => false
        };

        /// <summary>
        /// Said after Open or Close got no answer (<see cref="MayHaveReachedController"/>): why, and that the roof may be
        /// moving; <paramref name="how"/> stops it. A connection that failed after the command was sent is not "could not
        /// be reached".
        /// </summary>
        internal static string Unanswered(Exception error, string verb, string how)
        {
            var why = error is HttpRequestException ? UnansweredConnection : RoofCliContext.Classify(error).Message;
            return $"{why} The {verb} may have reached the controller, and the roof may be moving. To stop it, {how}.";
        }

        internal const string UnansweredConnection = "The connection to the controller ended before its answer arrived.";

        /// <summary>
        /// How long an interrupted Open or Close (Ctrl+C, or quitting the interface) waits for its answer before Stop is
        /// sent anyway. With the Stop timeout, it is well within <see cref="RoofCliTermination.StopGrace"/>.
        /// </summary>
        internal static readonly TimeSpan MotionAnswerWait = TimeSpan.FromSeconds(3);

        /// <summary>Said when an interrupted Open or Close was not answered before the Stop was sent.</summary>
        internal static string UnansweredBeforeStop(string command)
            => $"{command} was not answered, so it may still reach the controller after the Stop. Check the roof, and run '{CommandName} stop' if it moves.";

        /// <summary>
        /// Ctrl+C (or the terminal closing) during a motion command: sends Stop, reports it, and exits as interrupted.
        /// The process waits for the Stop's answer before it ends. <paramref name="unanswered"/> names an Open or Close
        /// cancelled without an answer, which may still reach the controller after the Stop.
        /// </summary>
        private static async Task<int> StopOnInterruptAsync(RoofCliContext context, RoofControllerClient client, string? unanswered = null)
        {
            var result = await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            var code = StopExitCode(result);
            if (context.Json)
            {
                context.WriteJson(new
                {
                    interrupted = true,
                    exitCode = (int)RoofExitCode.Interrupted,
                    commandAnswered = unanswered is null,
                    stop = DescribeStop(result, code)
                });
            }
            else
            {
                context.Host.Error.WriteLine("Interrupted: Stop sent.");
                (code == RoofExitCode.Success ? context.Out : context.Host.Error).WriteLine(result.Message);
                if (unanswered is not null)
                {
                    context.Host.Error.WriteLine(UnansweredBeforeStop($"The {unanswered}"));
                }
            }

            return (int)RoofExitCode.Interrupted;
        }

        /// <summary>How often a motion command reads the status over REST while the status hub is not delivering it.</summary>
        internal static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>How long a motion command follows the roof with no status from the controller before it gives up.</summary>
        internal static readonly TimeSpan NoStatusLimit = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Follows a motion until it ends, renewing the operator lease over REST when one applies. The status comes from
        /// the status hub; while the hub is not delivering it, the command reads it over REST every
        /// <see cref="StatusPollInterval"/>, and gives up (exit 4) after <see cref="NoStatusLimit"/> with no status at
        /// all. Ctrl+C sends Stop. A lease that cannot be renewed is reported: the controller stops the roof when it
        /// runs out.
        /// </summary>
        private static async Task<int> FollowMotionAsync(
            RoofCliContext context,
            RoofControllerClient client,
            RoofMotionDirection direction,
            RoofStatusResponse accepted,
            CancellationToken cancellationToken)
        {
            // Wakes the loop: a status from the hub, or a change of the hub's state (connected, stale, current again).
            var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            var updates = new ConcurrentQueue<RoofStatusResponse>();
            await using var feed = client.CreateStatusFeed();
            feed.StatusReceived += (_, e) =>
            {
                updates.Enqueue(e.Status);
                wake.Writer.TryWrite(true);
            };
            feed.StateChanged += (_, _) => wake.Writer.TryWrite(true);
            feed.Start();

            var time = context.Host.Time;
            var status = accepted;
            var lastStatusAt = time.GetUtcNow();
            var renewAt = NextRenewal(status);
            var lastLine = RoofCliFormat.DescribeRoof(status);

            // When to read the status over REST; null while the hub delivers it. The hub has StaleAfter to deliver
            // its first status.
            DateTimeOffset? readAt = lastStatusAt + context.StatusFeed.StaleAfter;
            var reading = false;
            try
            {
                while (true)
                {
                    if (IsLive(feed))
                    {
                        readAt = null;
                        if (reading)
                        {
                            reading = false;
                            context.Host.Error.WriteLine("Live status is connected again.");
                        }
                    }
                    else
                    {
                        readAt ??= time.GetUtcNow();
                    }

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var due = Earliest(renewAt, readAt);
                    var delay = due is { } at ? Max(at - time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
                    var timer = Task.Delay(delay, time, wait.Token);
                    var next = wake.Reader.WaitToReadAsync(wait.Token).AsTask();
                    await Task.WhenAny(timer, next).ConfigureAwait(false);
                    await wait.CancelAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    wake.Reader.TryRead(out _);

                    while (updates.TryDequeue(out var update))
                    {
                        lastStatusAt = time.GetUtcNow();
                        if (RoofStatusRules.ShouldApply(status, update))
                        {
                            status = update;
                        }
                    }

                    if (renewAt is { } renew && time.GetUtcNow() >= renew)
                    {
                        status = await RenewLeaseAsync(context, client, cancellationToken).ConfigureAwait(false);
                        lastStatusAt = time.GetUtcNow();
                        renewAt = null;
                    }
                    else if (!IsLive(feed) && readAt is { } read && time.GetUtcNow() >= read)
                    {
                        if (!reading)
                        {
                            reading = true;
                            context.Host.Error.WriteLine(
                                $"Live status is not connected: reading the status every {StatusPollInterval.TotalSeconds:0} s instead. Ctrl+C sends Stop.");
                        }

                        if (await ReadStatusAsync(client, cancellationToken).ConfigureAwait(false) is { } current)
                        {
                            lastStatusAt = time.GetUtcNow();
                            if (RoofStatusRules.ShouldApply(status, current))
                            {
                                status = current;
                            }
                        }
                        else if (time.GetUtcNow() - lastStatusAt >= NoStatusLimit)
                        {
                            throw new RoofCliRefusedException(
                                $"No status from the controller for {NoStatusLimit.TotalSeconds:0} s: the roof may still be moving. "
                                + $"To stop it, run '{CommandName} stop', or use the stop control at the roof.",
                                RoofExitCode.Unreachable);
                        }

                        readAt = time.GetUtcNow() + StatusPollInterval;
                    }

                    // A status can bring the renewal forward, never put it off: the loop wakes for each status and
                    // each change of the hub's state, and a renewal put off at every wake would never be sent.
                    renewAt = status.IsMoving ? Earliest(renewAt, NextRenewal(status)) : null;
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

        /// <summary>True while the hub delivers the status: connected, with a status that is not stale.</summary>
        private static bool IsLive(RoofStatusFeed feed)
            => feed.State == RoofStatusFeedState.Connected && feed.Status is not null && !feed.IsStale;

        /// <summary>Renews the lease; a lease that has already ended gives the status instead.</summary>
        private static async Task<RoofStatusResponse> RenewLeaseAsync(RoofCliContext context, RoofControllerClient client, CancellationToken cancellationToken)
        {
            try
            {
                return await client.Roof.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RoofApiException refusal) when (refusal.Code == RoofControllerErrorCode.LeaseNotActive)
            {
                return refusal.RoofStatus ?? await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsUnreachable(error, cancellationToken) || IsServerError(error))
            {
                throw new RoofCliRefusedException(
                    $"The lease could not be renewed: {RoofText.DescribeFailure(error)} If the controller is running, it stops the roof when the lease runs out.",
                    RoofExitCode.Unreachable,
                    error);
            }
        }

        /// <summary>
        /// Reads the status over REST; null when the controller could not be reached or answered with a server error
        /// (a proxy's 502 included), which says nothing about the roof.
        /// </summary>
        private static async Task<RoofStatusResponse?> ReadStatusAsync(RoofControllerClient client, CancellationToken cancellationToken)
        {
            try
            {
                return await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsUnreachable(error, cancellationToken) || IsServerError(error))
            {
                return null;
            }
        }

        private static bool IsUnreachable(Exception error, CancellationToken cancellationToken)
            => error is HttpRequestException or TimeoutException
                || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);

        /// <summary>An HTTP 5xx answer, a 503 included: the controller (or a proxy in front of it) could not answer.</summary>
        private static bool IsServerError(Exception error)
            => error is RoofApiException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError };

        private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right)
            => left is null ? right : right is null ? left : left < right ? left : right;

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
