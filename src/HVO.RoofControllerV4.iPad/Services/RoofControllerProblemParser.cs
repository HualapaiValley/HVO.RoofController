using System.Globalization;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Turns a non-success HTTP response into a <see cref="RoofControllerApiException"/>, reading RFC 7807 problem
/// details (<c>code</c> and <c>roofStatus</c> extensions) when the body has them.
/// </summary>
public static class RoofControllerProblemParser
{
    public static RoofControllerApiException CreateHttpFailure(int statusCode, string? reasonPhrase, string? body, string operation)
    {
        string? title = null;
        string? detail = null;
        string? rawCode = null;
        RoofControllerErrorCode? errorCode = null;
        RoofStatusResponse? roofStatus = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    title = GetString(root, "title");
                    detail = GetString(root, "detail");
                    (rawCode, errorCode) = ReadCode(root);
                    roofStatus = ReadRoofStatus(root);
                }
            }
            catch (JsonException)
            {
                // Not JSON (for example a proxy error page); the status code alone describes the failure.
            }
        }

        var kind = statusCode switch
        {
            401 => RoofControllerFailureKind.Unauthorized,
            403 => RoofControllerFailureKind.Forbidden,
            _ => RoofControllerFailureKind.Rejected
        };

        var summary = detail ?? title ?? reasonPhrase ?? "no details";
        var codeText = rawCode is null ? string.Empty : $" {rawCode}";
        var message = $"{operation}: HTTP {statusCode}{codeText} ({summary})";

        return new RoofControllerApiException(kind, message)
        {
            StatusCode = statusCode,
            ErrorCode = errorCode,
            RawCode = rawCode,
            RoofStatus = roofStatus,
            ProblemTitle = title,
            ProblemDetail = detail
        };
    }

    private static (string? RawCode, RoofControllerErrorCode? Code) ReadCode(JsonElement root)
    {
        if (TryGetProperty(root, RoofControllerApiContract.ProblemCodeExtension, out var codeElement))
        {
            switch (codeElement.ValueKind)
            {
                case JsonValueKind.String:
                    var raw = codeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        return (raw, ParseCode(raw));
                    }

                    break;
                case JsonValueKind.Number when codeElement.TryGetInt32(out var numeric):
                    var numericCode = (RoofControllerErrorCode)numeric;
                    return Enum.IsDefined(numericCode)
                        ? (numericCode.ToString(), numericCode)
                        : (numeric.ToString(CultureInfo.InvariantCulture), null);
            }
        }

        var type = GetString(root, "type");
        if (type is not null && type.StartsWith(RoofControllerApiContract.ProblemTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = type[RoofControllerApiContract.ProblemTypePrefix.Length..];
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return (suffix, ParseCode(suffix));
            }
        }

        return (null, null);
    }

    private static RoofControllerErrorCode? ParseCode(string raw)
    {
        // Only accept names; a numeric string would otherwise parse to any value.
        if (raw.Length > 0 && !char.IsDigit(raw[0]) && raw[0] != '-'
            && Enum.TryParse<RoofControllerErrorCode>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static RoofStatusResponse? ReadRoofStatus(JsonElement root)
    {
        if (!TryGetProperty(root, RoofControllerApiContract.ProblemStatusExtension, out var statusElement)
            || statusElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return statusElement.Deserialize<RoofStatusResponse>(RoofControllerJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
        => TryGetProperty(root, name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
