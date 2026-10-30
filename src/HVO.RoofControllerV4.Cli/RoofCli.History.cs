using System.CommandLine;
using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        internal static readonly string[] HistoryHeaders = [.. RoofHistoryText.Headers.Select(header => header.ToUpperInvariant())];

        private Command CreateHistoryCommand()
        {
            var user = new Option<string?>("--user", "-u")
            {
                Description = "Only the commands of this person or key, or sent for them (admin; anyone else reads only their own)."
            };
            var since = new Option<string?>("--since")
            {
                Description = "Only commands since then: a date or time (ISO 8601, UTC unless it says), or how long ago (30m, 12h, 7d)."
            };
            var until = new Option<string?>("--until")
            {
                Description = "Only commands before then, given as for --since."
            };
            var take = new Option<int>("--take", "-n")
            {
                Description = $"How many commands to show, newest first: 1 to {RoofCommandHistoryContract.MaximumTake} (default {RoofCommandHistoryContract.DefaultTake}).",
                DefaultValueFactory = _ => RoofCommandHistoryContract.DefaultTake
            };
            take.Validators.Add(result =>
            {
                if (result.GetValueOrDefault<int>() is < 1 or > RoofCommandHistoryContract.MaximumTake)
                {
                    result.AddError($"--take must be from 1 to {RoofCommandHistoryContract.MaximumTake}.");
                }
            });
            var skip = new Option<int>("--skip")
            {
                Description = "How many of the newest commands to skip, to read older ones."
            };
            skip.Validators.Add(result =>
            {
                if (result.GetValueOrDefault<int>() < 0)
                {
                    result.AddError("--skip must not be negative.");
                }
            });

            var command = new Command(
                "history",
                "The roof commands the controller was sent, accepted or refused, newest first: yours, or everyone's for an admin.")
            {
                user, since, until, take, skip
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var now = context.Host.Time.GetUtcNow();
                var query = new RoofHistoryQuery
                {
                    User = parseResult.GetValue(user) is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null,
                    From = parseResult.GetValue(since) is { } from ? ParseHistoryTime(from, now, "--since") : null,
                    To = parseResult.GetValue(until) is { } to ? ParseHistoryTime(to, now, "--until") : null,
                    Skip = parseResult.GetValue(skip),
                    Take = parseResult.GetValue(take)
                };
                if (query.From is { } start && query.To is { } end && end < start)
                {
                    throw new RoofCliUsageException("--until is before --since.");
                }

                using var client = context.Connect();
                var page = await client.Roof.GetHistoryAsync(query, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(page);
                    return (int)RoofExitCode.Success;
                }

                context.Out.WriteLine(RoofHistoryText.DescribeScope(page));
                if (page.Entries.Count == 0)
                {
                    context.Out.WriteLine(query.User is null && query.From is null && query.To is null ? RoofHistoryText.Empty : RoofHistoryText.EmptyFiltered);
                }
                else
                {
                    RoofCliFormat.WriteTable(context.Out, HistoryHeaders, page.Entries.Select(RoofHistoryText.DescribeRow));
                    context.Out.WriteLine(RoofHistoryText.DescribeRange(page));
                }

                context.Out.WriteLine(RoofHistoryText.DescribeRetention(page));
                return (int)RoofExitCode.Success;
            });
            return command;
        }

        /// <summary>
        /// A time for <c>history --since</c> or <c>--until</c>: an ISO 8601 date or time (UTC unless it gives an offset), or
        /// how long before <paramref name="now"/> as a number and m, h or d.
        /// </summary>
        internal static DateTimeOffset ParseHistoryTime(string text, DateTimeOffset now, string option)
        {
            var value = text.Trim();
            if (value.Length >= 2 && char.ToLowerInvariant(value[^1]) is 'm' or 'h' or 'd'
                && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                var ago = char.ToLowerInvariant(value[^1]) switch
                {
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    _ => TimeSpan.FromDays(amount)
                };
                return now - ago;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            {
                return time;
            }

            throw new RoofCliUsageException(
                $"{option} '{text}' is not a time. Give a date or time such as 2026-10-30 or 2026-10-30T18:00Z, or how long ago, such as 30m, 12h or 7d.");
        }
    }
}
