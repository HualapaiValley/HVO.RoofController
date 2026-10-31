using System.Text.Json;

namespace HVO.RoofControllerV4.Alpaca.Protocol;

/// <summary>Writes a <c>Value</c> into an Alpaca answer.</summary>
public delegate void AlpacaValueWriter(Utf8JsonWriter writer);

/// <summary>
/// What a device member returns: a value (a property), nothing (a method), or an Alpaca error (with a property's
/// fallback value). Every one is answered with HTTP 200 and the standard envelope.
/// </summary>
public readonly record struct AlpacaResult(AlpacaValueWriter? Value, int ErrorNumber, string ErrorMessage)
{
    public static AlpacaResult Done { get; } = new(null, 0, string.Empty);

    public static AlpacaResult Of(bool value) => new(writer => writer.WriteBooleanValue(value), 0, string.Empty);

    public static AlpacaResult Of(int value) => new(writer => writer.WriteNumberValue(value), 0, string.Empty);

    public static AlpacaResult Of(string value) => new(writer => writer.WriteStringValue(value), 0, string.Empty);

    public static AlpacaResult Of(IReadOnlyList<string> values) => new(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }, 0, string.Empty);

    public static AlpacaResult Error(int number, string message) => new(null, number, message);

    public static AlpacaResult Error(AlpacaException error) => new(null, error.Number, error.Message);
}

/// <summary>
/// The Alpaca response envelope: <c>Value</c> (when there is one), <c>ClientTransactionID</c> (the client's, echoed,
/// or 0), <c>ServerTransactionID</c> (counting up from 1), <c>ErrorNumber</c> and <c>ErrorMessage</c> (0 and empty on
/// success), cased exactly so. A malformed request is answered with HTTP 400 and plain text instead.
/// </summary>
public static class AlpacaResponse
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Writes a device member's answer.</summary>
    public static async Task WriteAsync(HttpContext context, uint clientTransactionId, uint serverTransactionId, AlpacaResult result)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        if (result.Value is { } value)
        {
            writer.WritePropertyName("Value");
            value(writer);
        }

        writer.WriteNumber("ClientTransactionID", clientTransactionId);
        writer.WriteNumber("ServerTransactionID", serverTransactionId);
        writer.WriteNumber("ErrorNumber", result.ErrorNumber);
        writer.WriteString("ErrorMessage", result.ErrorMessage);
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a management API answer: <c>Value</c>, <c>ClientTransactionID</c> and <c>ServerTransactionID</c>, as the
    /// Alpaca management API defines them.
    /// </summary>
    public static async Task WriteManagementAsync(HttpContext context, uint clientTransactionId, uint serverTransactionId, AlpacaValueWriter value)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WritePropertyName("Value");
        value(writer);
        writer.WriteNumber("ClientTransactionID", clientTransactionId);
        writer.WriteNumber("ServerTransactionID", serverTransactionId);
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Answers a malformed request: HTTP 400 with <paramref name="message"/> as plain text.</summary>
    public static Task BadRequestAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}

/// <summary>The server's transaction numbers: one per answer, counting up from 1 for the life of the process.</summary>
public sealed class AlpacaTransactions
{
    private long _last;

    /// <summary>The next number; after 4294967295 it starts again from 1.</summary>
    public uint Next()
    {
        var next = (ulong)Interlocked.Increment(ref _last);
        return (uint)((next - 1) % uint.MaxValue) + 1;
    }
}
