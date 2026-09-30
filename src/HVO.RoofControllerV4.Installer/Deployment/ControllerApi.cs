using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>What the controller answered: its HTTP status and body. <see cref="Status"/> is null when it did not answer, and <see cref="Reason"/> says why.</summary>
public sealed record ControllerResponse(int? Status, string Body, string? Reason = null)
{
    /// <summary>The problem detail's <c>detail</c> (or <c>title</c>) in the body, when the controller sent one.</summary>
    public string? Problem
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Body);
                var root = document.RootElement;
                return root.ValueKind != JsonValueKind.Object ? null
                    : root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()
                    : root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Calls the running controller's API from inside its container, over its loopback on plain HTTP, as the deploy script
/// does: no certificate or host name to trust, and nothing published is needed. curl reads the key and any body from its
/// standard input (<c>-K -</c>), never from a command line, and the command is secret, so the log shows neither the
/// key, nor the body, nor the answer.
/// </summary>
public static class ControllerApi
{
    /// <summary>The controller's API as its container reaches it.</summary>
    public const string Address = "http://localhost:8080/";

    /// <summary>
    /// Sends <paramref name="json"/> (a POST) or nothing (a GET) to <paramref name="path"/> with <paramref name="key"/>.
    /// Every secret in the body must already be registered with the log.
    /// </summary>
    public static Task<ControllerResponse> SendAsync(InstallContext context, string container, string key, string path, string? json, CancellationToken cancellationToken)
        => SendAsync(context, container, key, json is null ? HttpMethod.Get : HttpMethod.Post, path, json, cancellationToken);

    /// <summary>
    /// Sends <paramref name="json"/>, or nothing, to <paramref name="path"/> as <paramref name="method"/> with
    /// <paramref name="key"/>. Every secret in the body must already be registered with the log.
    /// </summary>
    public static async Task<ControllerResponse> SendAsync(InstallContext context, string container, string key, HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(method);
        context.Log.AddSecret(key);
        var config = new StringBuilder();
        if (method != (json is null ? HttpMethod.Get : HttpMethod.Post))
        {
            Option(config, "request", method.Method);
        }

        Option(config, "header", $"X-Api-Key: {key}");
        Option(config, "header", "Accept: application/json");
        if (json is not null)
        {
            // data-raw takes the body as it is (-d would read a file for a body that starts with @).
            Option(config, "header", "Content-Type: application/json");
            Option(config, "data-raw", json);
        }

        var result = await context.Machine.Commands.RunAsync(
            new CommandLine("docker", "exec", "-i", container, "curl", "-sS", "--max-time", "15", "-K", "-", "-w", "\n%{http_code}", Address + path)
            {
                Input = config.ToString(),
                Secret = true,
                Timeout = TimeSpan.FromSeconds(30)
            },
            cancellationToken).ConfigureAwait(false);

        var output = result.Output.TrimEnd('\n');
        var end = output.LastIndexOf('\n');
        var code = (end < 0 ? output : output[(end + 1)..]).Trim();
        if (!result.Succeeded || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status) || status == 0)
        {
            var reason = context.Log.Redact(result.Reason);
            return new ControllerResponse(null, string.Empty, string.IsNullOrWhiteSpace(reason) ? $"docker exec ended with exit {result.ExitCode}" : reason);
        }

        return new ControllerResponse(status, end < 0 ? string.Empty : output[..end]);
    }

    // A line of curl's config: the value in quotes, with its backslashes and quotes escaped as curl reads them.
    private static void Option(StringBuilder config, string name, string value)
        => config.Append(name).Append(" = \"")
            .Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal))
            .Append("\"\n");
}
