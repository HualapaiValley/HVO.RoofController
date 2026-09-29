using System.Net;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// A request the controller answered with an error. Carries the ProblemDetails fields: the <c>code</c> the controller
/// named, its status snapshot for roof commands, validation errors, and <c>Retry-After</c>.
/// <see cref="Exception.Message"/> is the shared wording from <see cref="RoofText.DescribeRefusal"/>.
/// </summary>
public sealed class RoofApiException : Exception
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public RoofApiException(
        HttpStatusCode statusCode,
        RoofControllerErrorCode? code = null,
        string? codeText = null,
        string? title = null,
        string? detail = null,
        RoofStatusResponse? roofStatus = null,
        TimeSpan? retryAfter = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        string? traceId = null)
        : base(RoofText.DescribeRefusal((int)statusCode, code, codeText))
    {
        StatusCode = statusCode;
        Code = code;
        CodeText = codeText ?? code?.ToString();
        Title = title;
        Detail = detail;
        RoofStatus = roofStatus;
        RetryAfter = retryAfter;
        Errors = errors ?? NoErrors;
        TraceId = traceId;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The controller's error code, when the answer named one this client knows.</summary>
    public RoofControllerErrorCode? Code { get; }

    /// <summary>The <c>code</c> exactly as sent, including middleware codes such as <c>https_required</c>.</summary>
    public string? CodeText { get; }

    public string? Title { get; }

    /// <summary>The controller's own explanation, for example which settings were refused. May be shown as a second line.</summary>
    public string? Detail { get; }

    /// <summary>The roof's status when a roof command was refused.</summary>
    public RoofStatusResponse? RoofStatus { get; }

    /// <summary>How long the controller asked the client to wait before retrying.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Validation errors by field, for a 400 answer.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public string? TraceId { get; }

    /// <summary>
    /// Reads an error answer. A body that is not ProblemDetails (a proxy page, an empty body) still gives an exception
    /// described by its status.
    /// </summary>
    public static async Task<RoofApiException> FromResponseAsync(HttpResponseMessage response, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        RoofControllerErrorCode? code = null;
        string? codeText = null;
        string? title = null;
        string? detail = null;
        string? traceId = null;
        RoofStatusResponse? roofStatus = null;
        Dictionary<string, string[]>? errors = null;

        try
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    codeText = ReadString(root, RoofControllerApiContract.ProblemCodeExtension);
                    if (codeText is not null && !int.TryParse(codeText, out _)
                        && Enum.TryParse<RoofControllerErrorCode>(codeText, ignoreCase: false, out var parsed)
                        && Enum.IsDefined(parsed))
                    {
                        code = parsed;
                    }

                    title = ReadString(root, "title");
                    detail = ReadString(root, "detail");
                    traceId = ReadString(root, "traceId");

                    if (root.TryGetProperty(RoofControllerApiContract.ProblemStatusExtension, out var status)
                        && status.ValueKind == JsonValueKind.Object)
                    {
                        roofStatus = status.Deserialize<RoofStatusResponse>(RoofClientJson.Options);
                    }

                    if (root.TryGetProperty("errors", out var fieldErrors) && fieldErrors.ValueKind == JsonValueKind.Object)
                    {
                        errors = fieldErrors.Deserialize<Dictionary<string, string[]>>(RoofClientJson.Options);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not ProblemDetails (for example a proxy error page): described by status alone.
        }
        catch (NotSupportedException)
        {
        }

        return new RoofApiException(
            response.StatusCode,
            code,
            codeText,
            title,
            detail,
            roofStatus,
            ReadRetryAfter(response, timeProvider ?? TimeProvider.System),
            errors,
            traceId);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, TimeProvider timeProvider)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
