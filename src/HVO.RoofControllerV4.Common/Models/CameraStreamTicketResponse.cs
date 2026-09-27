using System;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// A short-lived, single-camera ticket for clients that cannot send an <c>X-Api-Key</c> header with a stream request,
/// such as an MJPEG <c>&lt;img&gt;</c> or a WebView. <see cref="Url"/> is relative to the controller base address.
/// The ticket only authorizes starting a stream before <see cref="ExpiresUtc"/>; it grants nothing else.
/// </summary>
public sealed record CameraStreamTicketResponse(string Url, DateTimeOffset ExpiresUtc);
