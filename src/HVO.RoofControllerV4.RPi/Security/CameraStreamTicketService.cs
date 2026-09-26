using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Outcome of validating a camera stream ticket.</summary>
public enum CameraStreamTicketValidation
{
    Valid = 0,
    Malformed = 1,
    BadSignature = 2,
    Expired = 3,
    WrongCamera = 4
}

/// <summary>
/// Issues short-lived camera stream tickets so clients that cannot attach headers to an MJPEG image request (the iPad
/// WebView) can start a stream. A ticket is HMAC-SHA256 signed with a random per-process key, bound to one camera id and
/// valid for <see cref="TicketLifetime"/> to start a stream (an already running stream is not cut off at expiry).
/// Tickets become invalid when the process restarts.
/// </summary>
public sealed class CameraStreamTicketService
{
    /// <summary>How long a ticket can be used to start a stream.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    private const string Version = "1";
    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;

    public CameraStreamTicketService(TimeProvider timeProvider)
        : this(timeProvider, RandomNumberGenerator.GetBytes(32))
    {
    }

    internal CameraStreamTicketService(TimeProvider timeProvider, byte[] key)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("The ticket key must be at least 32 bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>Issues a ticket for <paramref name="cameraId"/> and returns the relative stream URL that carries it.</summary>
    public CameraStreamTicketResponse Issue(int cameraId)
    {
        var expires = _timeProvider.GetUtcNow().Add(TicketLifetime);
        var expiresSeconds = expires.ToUnixTimeSeconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var payload = string.Create(CultureInfo.InvariantCulture, $"{Version}|{cameraId}|{expiresSeconds}|{nonce}");
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var ticket = Base64Url.EncodeToString(payloadBytes) + "." + Base64Url.EncodeToString(Sign(payloadBytes));
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1.0/Camera/{cameraId}/mjpeg?{RoofControllerApiContract.CameraTicketQueryParameter}={Uri.EscapeDataString(ticket)}");

        return new CameraStreamTicketResponse(url, DateTimeOffset.FromUnixTimeSeconds(expiresSeconds));
    }

    /// <summary>Validates <paramref name="ticket"/> for <paramref name="cameraId"/> at the current time.</summary>
    public CameraStreamTicketValidation Validate(string? ticket, int cameraId)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 512)
        {
            return CameraStreamTicketValidation.Malformed;
        }

        var separator = ticket.IndexOf('.');
        if (separator <= 0 || separator == ticket.Length - 1 || ticket.IndexOf('.', separator + 1) >= 0)
        {
            return CameraStreamTicketValidation.Malformed;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64Url.DecodeFromChars(ticket.AsSpan(0, separator));
            signature = Base64Url.DecodeFromChars(ticket.AsSpan(separator + 1));
        }
        catch (FormatException)
        {
            return CameraStreamTicketValidation.Malformed;
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(payloadBytes), signature))
        {
            return CameraStreamTicketValidation.BadSignature;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 4
            || parts[0] != Version
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticketCameraId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresSeconds))
        {
            return CameraStreamTicketValidation.Malformed;
        }

        if (ticketCameraId != cameraId)
        {
            return CameraStreamTicketValidation.WrongCamera;
        }

        if (_timeProvider.GetUtcNow().ToUnixTimeSeconds() > expiresSeconds)
        {
            return CameraStreamTicketValidation.Expired;
        }

        return CameraStreamTicketValidation.Valid;
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(_key, payload);
}
